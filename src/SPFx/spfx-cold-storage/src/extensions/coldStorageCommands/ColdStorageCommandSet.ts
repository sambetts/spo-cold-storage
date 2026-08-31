import { Log } from '@microsoft/sp-core-library';
import { BaseListViewCommandSet, Command, IListViewCommandSetExecuteEventParameters, IListViewCommandSetListViewUpdatedParameters } from '@microsoft/sp-listview-extensibility';
import { AadHttpClient } from '@microsoft/sp-http';

import { ColdStorageApiClient, ColdStorageApiError, IContainerResponse, IStartMigrationItem } from '../../common/ColdStorageApiClient';import { MigrationProgressDialog } from './MigrationProgressDialog';
import { formatNumber } from '../../common/statusFormat';

export interface IColdStorageCommandSetProperties {
  apiBaseUrl: string;
  apiAppIdUri: string;
}

const LOG_SOURCE = 'ColdStorageCommandSet';

/**
 * ListView Command Set that adds Migrate/Restore commands to a document
 * library toolbar. The web API enforces the site-collection-owner check, so
 * here we just hide the commands when the user has nothing selected.
 *
 * Every code path opens a {@link MigrationProgressDialog} as the very first
 * action so the user always gets visual confirmation that their click landed
 * and so that errors (network, auth, server) surface in the UI instead of
 * disappearing into an unhandled async rejection.
 */
export default class ColdStorageCommandSet extends BaseListViewCommandSet<IColdStorageCommandSetProperties> {
  private apiClient?: ColdStorageApiClient;
  private activeDialog?: MigrationProgressDialog;

  public onInit(): Promise<void> {
    Log.info(LOG_SOURCE, 'Initializing ColdStorageCommandSet.');
    if (!this.properties.apiBaseUrl) {
      Log.warn(LOG_SOURCE, 'apiBaseUrl not configured; cold storage commands will be hidden.');
      return Promise.resolve();
    }
    return this.context.aadHttpClientFactory
      .getClient(this.properties.apiAppIdUri)
      .then((client: AadHttpClient) => {
        this.apiClient = new ColdStorageApiClient(client, this.properties.apiBaseUrl);
      });
  }

  public onListViewUpdated(event: IListViewCommandSetListViewUpdatedParameters): void {
    const migrate: Command = this.tryGetCommand('COLDSTORAGE_MIGRATE');
    const restore: Command = this.tryGetCommand('COLDSTORAGE_RESTORE');
    const status:  Command = this.tryGetCommand('COLDSTORAGE_STATUS');
    const hasSelection = event.selectedRows.length > 0;
    const isFolderRow = (r: typeof event.selectedRows[number]) => (r.getValueByName('FSObjType') as string) === '1';
    const isPlaceholderRow = (r: typeof event.selectedRows[number]) => (r.getValueByName('FileLeafRef') as string).endsWith('.url');
    const anyArePlaceholders = hasSelection && event.selectedRows.some(isPlaceholderRow);
    const anyAreFolders = hasSelection && event.selectedRows.some(isFolderRow);

    if (migrate) {
      // Hide for placeholder selections - migrating a .url back to cold storage is nonsensical
      // and the API would either reject or create a confusing nested placeholder.
      migrate.visible = hasSelection && !!this.apiClient && !anyArePlaceholders;
    }
    if (restore) {
      // Restore makes sense for cold-storage placeholders, or a folder (expanded
      // server-side to the archived items beneath it) — issue #9.
      restore.visible = hasSelection && !!this.apiClient && (anyArePlaceholders || anyAreFolders);
    }
    if (status) {
      // Always available so users can review past / in-flight jobs without first picking a file.
      status.visible = !!this.apiClient;
    }
  }

  public onExecute(event: IListViewCommandSetExecuteEventParameters): void {
    if (!this.apiClient) {
      // No dialog because onInit didn't wire the client - log and bail. The
      // command should already be hidden via onListViewUpdated, so this is
      // really just a guard.
      Log.warn(LOG_SOURCE, 'API client not initialized; ignoring command.');
      return;
    }
    if (event.itemId === 'COLDSTORAGE_MIGRATE') {
      void this.runMigrate(event);
    } else if (event.itemId === 'COLDSTORAGE_RESTORE') {
      void this.runRestore(event);
    } else if (event.itemId === 'COLDSTORAGE_STATUS') {
      void this.runStatus();
    }
  }

  // ---- Migrate ----

  private async runMigrate(event: IListViewCommandSetExecuteEventParameters): Promise<void> {
    const client = this.apiClient;
    if (!client) return;
    const dialog = this.openDialog('Migrate');
    dialog.setStatusMessage('Getting things ready…');

    let target: IContainerResponse | undefined;
    try {
      const containers = await client.listContainers();
      target = containers.find(c => c.canMigrate);
    } catch (err) {
      dialog.showError(this.describeError(err, 'We couldn’t get things ready just now.'), () => this.runMigrate(event));
      return;
    }

    if (!target) {
      dialog.showError('You don’t have permission to archive files here. Ask your site owner if you need access.');
      return;
    }
    const container = target;

    const siteUrl = this.context.pageContext.site.absoluteUrl;
    const webUrl = this.context.pageContext.web.absoluteUrl;
    const items: IStartMigrationItem[] = event.selectedRows.map(row => ({
      serverRelativeUrl: row.getValueByName('FileRef') as string,
      itemKind: (row.getValueByName('FSObjType') as string) === '1' ? 'Folder' : 'File',
      fileSize: Number(row.getValueByName('File_x0020_Size')) || undefined,
      // SharePoint's `Modified` field comes back as a locale-formatted display
      // string (e.g. "12/5/2025 10:29 AM"), not ISO 8601 - System.Text.Json on
      // the server can't deserialize that into a DateTime?. Try to parse it
      // into an ISO timestamp; if anything goes wrong just omit the field
      // (the server treats lastModified as optional metadata anyway).
      lastModified: ColdStorageCommandSet.tryParseIsoDate(row.getValueByName('Modified')),
    }));
    const submit = async (copyMetadataColumns: boolean): Promise<void> => {
      dialog.setStatusMessage(`Getting ${formatNumber(items.length)} item${items.length === 1 ? '' : 's'} ready…`);
      try {
        const response = await client.startMigration({
          siteUrl, webUrl,
          containerName: container.name,
          recursive: true,
          copyMetadataColumns,
          items,
        });
        dialog.addAcceptedJob(response.jobId, response, `Archiving ${formatNumber(items.length)} item${items.length === 1 ? '' : 's'}`);
      } catch (err) {
        dialog.showError(this.describeError(err, 'We couldn’t start archiving just now.'), () => { void submit(copyMetadataColumns); });
      }
    };

    // Confirm before submitting — archiving replaces the file with a shortcut.
    dialog.confirm({
      message: 'These will be moved to long-term storage to free up space here. Each one is replaced by a shortcut you can open any time to get the file back. Your file is only removed once the stored copy has been checked. Folders include everything inside them.',
      confirmLabel: `Archive ${formatNumber(items.length)} item${items.length === 1 ? '' : 's'}`,
      items: items.map(i => ({ name: ColdStorageCommandSet.basename(i.serverRelativeUrl), kind: i.itemKind })),
      metadataOption: {
        label: 'Also show who created and last changed each file',
        note: 'Adds “Original Author”, “Original Editor” and “Original Modified” columns to this library so that information stays visible after archiving. Either way it is kept with the stored file and comes back with it.',
        defaultChecked: false,
      },
    }, (result) => { void submit(result.copyMetadataColumns); });
  }

  // ---- Restore ----

  private async runRestore(event: IListViewCommandSetExecuteEventParameters): Promise<void> {
    const client = this.apiClient;
    if (!client) return;
    const dialog = this.openDialog('Restore');

    const siteUrl = this.context.pageContext.site.absoluteUrl;
    const webUrl = this.context.pageContext.web.absoluteUrl;

    // Split the selection into explicit placeholders and folders. Folders are
    // expanded server-side to the archived items beneath them, and the whole lot
    // is restored as one job so the user sees aggregated progress (issue #9).
    const placeholders: string[] = [];
    const folders: string[] = [];
    for (const row of event.selectedRows) {
      const ref = row.getValueByName('FileRef') as string;
      if ((row.getValueByName('FSObjType') as string) === '1') {
        folders.push(ref);
      } else if (ref.endsWith('.url')) {
        placeholders.push(ref);
      }
    }

    if (placeholders.length === 0 && folders.length === 0) {
      dialog.showError('Select one or more archived files (or a folder) to bring back.');
      return;
    }

    const submit = async (): Promise<void> => {
      dialog.setStatusMessage(
        folders.length > 0
          ? `Getting ${formatNumber(placeholders.length)} file${placeholders.length === 1 ? '' : 's'} + ${formatNumber(folders.length)} folder${folders.length === 1 ? '' : 's'} ready…`
          : `Getting ${formatNumber(placeholders.length)} item${placeholders.length === 1 ? '' : 's'} ready…`,
      );
      try {
        const response = await client.startBatchRestore({
          siteUrl,
          webUrl,
          placeholders,
          folderServerRelativeUrls: folders,
          conflictBehavior: 'Fail',
        });
        // Submit is async: the server returns immediately and resolves the folder(s) + queues the
        // per-file items in the background (a large folder previously blocked the request until it
        // timed out with "Failed to fetch"). Track the job by id — its progress column fills in as
        // expansion completes — and don't treat the (not-yet-known) count of 0 as "no items".
        const restoreLabel = folders.length > 0
          ? `Bringing back ${formatNumber(folders.length)} folder${folders.length === 1 ? '' : 's'}${placeholders.length > 0 ? ` + ${formatNumber(placeholders.length)} file${placeholders.length === 1 ? '' : 's'}` : ''}`
          : `Bringing back ${formatNumber(placeholders.length)} item${placeholders.length === 1 ? '' : 's'}`;
        dialog.addAcceptedJob(response.jobId, response, restoreLabel);
      } catch (err) {
        dialog.showError(this.describeError(err, "We couldn’t start bringing your files back."), () => { void submit(); });
      }
    };

    // Confirm before submitting — restore brings the file(s) back and removes the placeholder.
    const confirmItems = [
      ...placeholders.map(p => ({ name: ColdStorageCommandSet.basename(p).replace(/\.url$/i, ''), kind: 'File' as const })),
      ...folders.map(f => ({ name: ColdStorageCommandSet.basename(f), kind: 'Folder' as const })),
    ];
    dialog.confirm({
      message: 'These will be brought back into this library, replacing their shortcuts with the real files. Folders bring back everything archived inside them.',
      confirmLabel: `Restore ${formatNumber(confirmItems.length)} item${confirmItems.length === 1 ? '' : 's'}`,
      items: confirmItems,
    }, () => { void submit(); });
  }

  // ---- Status (browse jobs) ----

  private async runStatus(): Promise<void> {
    const client = this.apiClient;
    if (!client) return;
    const dialog = this.openDialog('Status');
    const siteUrl = this.context.pageContext.site.absoluteUrl;
    dialog.setStatusMessage('Looking up recent activity…');
    try {
      const jobs = await client.listRecentJobs(siteUrl, 20);
      dialog.showJobList(jobs);
    } catch (err) {
      dialog.showError(this.describeError(err, 'We couldn’t load recent activity just now.'), () => this.runStatus());
    }
  }

  // ---- Helpers ----

  private openDialog(operation: 'Migrate' | 'Restore' | 'Status'): MigrationProgressDialog {
    // Close any previous dialog so we never leak overlays / timers.
    this.activeDialog?.close();
    const dialog = new MigrationProgressDialog(this.apiClient!, operation);
    const opening = operation === 'Migrate' ? 'Getting ready…'
                  : operation === 'Restore' ? 'Preparing restore…'
                  : 'Loading recent jobs…';
    dialog.open(opening);
    this.activeDialog = dialog;
    return dialog;
  }

  /**
   * Turns any error into something an ordinary site user can act on (issue #70).
   *
   * End users can do nothing with "HTTP 502" or a server stack trace, and showing
   * it just alarms them — so we say what happened in their terms and, crucially,
   * that their files are unaffected. The technical detail still goes to the
   * browser console for whoever supports them.
   */
  private describeError(err: unknown, friendlyPrefix: string): string {
    // Keep the real detail available without putting it in front of the user.
    // eslint-disable-next-line no-console
    console.error('[Cold storage]', friendlyPrefix, err);

    if (err instanceof ColdStorageApiError) {
      if (err.isUnauthorized) {
        return 'You don’t have permission to do this here. Ask your site owner if you need access, or refresh the page and sign in again.';
      }
      if (err.isThrottled) {
        return 'SharePoint is busy at the moment. Please try again in a minute.';
      }
      if (err.isServerError) {
        return `${friendlyPrefix} Please try again in a few minutes.`;
      }
      if (err.isTransport) {
        return 'We couldn’t reach the archive service. Check your connection and try again.';
      }
      // A 4xx we don't specifically recognise: the server's message is written for
      // callers of the API, so prefer our own wording.
      return `${friendlyPrefix} Please try again.`;
    }
    return `${friendlyPrefix} Please try again.`;
  }


  private static tryParseIsoDate(value: unknown): string | undefined {
    if (value === null || value === undefined || value === '') return undefined;
    const s = typeof value === 'string' ? value : String(value);
    const d = new Date(s);
    return isNaN(d.getTime()) ? undefined : d.toISOString();
  }

  private static basename(serverRelativeUrl: string): string {
    if (!serverRelativeUrl) return '';
    const trimmed = serverRelativeUrl.replace(/\/+$/, '');
    const idx = trimmed.lastIndexOf('/');
    return idx >= 0 ? trimmed.substring(idx + 1) : trimmed;
  }
}
