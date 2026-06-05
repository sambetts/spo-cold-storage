import { Log } from '@microsoft/sp-core-library';
import { BaseListViewCommandSet, Command, IListViewCommandSetExecuteEventParameters, IListViewCommandSetListViewUpdatedParameters } from '@microsoft/sp-listview-extensibility';
import { AadHttpClient } from '@microsoft/sp-http';

import { ColdStorageApiClient, ColdStorageApiError, IStartMigrationItem } from '../../common/ColdStorageApiClient';
import { MigrationProgressDialog } from './MigrationProgressDialog';

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
    const hasSelection = event.selectedRows.length > 0;
    if (migrate) {
      migrate.visible = hasSelection && !!this.apiClient;
    }
    if (restore) {
      restore.visible = hasSelection && !!this.apiClient
        && event.selectedRows.every(r => (r.getValueByName('FileLeafRef') as string).endsWith('.url'));
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
    }
  }

  // ---- Migrate ----

  private async runMigrate(event: IListViewCommandSetExecuteEventParameters): Promise<void> {
    const client = this.apiClient;
    if (!client) return;
    const dialog = this.openDialog('Migrate');
    dialog.setStatusMessage('Looking up available cold-storage containers…');

    let target;
    try {
      const containers = await client.listContainers();
      target = containers.find(c => c.canMigrate);
    } catch (err) {
      dialog.showError(this.describeError(err, 'Could not load cold-storage containers'), () => this.runMigrate(event));
      return;
    }

    if (!target) {
      dialog.showError('You do not have permission to migrate to any configured cold-storage container.');
      return;
    }

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

    dialog.setStatusMessage(`Submitting migration for ${items.length} item${items.length === 1 ? '' : 's'} to container "${target.displayName ?? target.name}"…`);

    try {
      const response = await client.startMigration({
        siteUrl, webUrl,
        containerName: target.name,
        recursive: true,
        items,
      });
      dialog.addAcceptedJob(response.jobId, response, `Migration job (${items.length} item${items.length === 1 ? '' : 's'})`);
    } catch (err) {
      dialog.showError(this.describeError(err, 'Failed to submit migration'), () => this.runMigrate(event));
    }
  }

  // ---- Restore ----

  private async runRestore(event: IListViewCommandSetExecuteEventParameters): Promise<void> {
    const client = this.apiClient;
    if (!client) return;
    const dialog = this.openDialog('Restore');
    dialog.setStatusMessage(`Resolving ${event.selectedRows.length} placeholder${event.selectedRows.length === 1 ? '' : 's'}…`);

    const siteUrl = this.context.pageContext.site.absoluteUrl;
    const webUrl = this.context.pageContext.web.absoluteUrl;
    const skipped: string[] = [];
    let submitted = 0;

    for (const row of event.selectedRows) {
      if (!dialog.isOpen) return; // user closed the dialog - stop submitting
      const placeholder = row.getValueByName('FileRef') as string;
      dialog.setStatusMessage(`Resolving ${placeholder}…`);

      let metadata;
      try {
        metadata = await client.resolvePlaceholder(placeholder);
      } catch (err) {
        skipped.push(`${placeholder}: ${this.describeError(err, 'resolve failed')}`);
        continue;
      }

      if (!metadata.isEligibleForRestore) {
        skipped.push(`${placeholder}: ${metadata.unavailableReason ?? 'not eligible for restore'}`);
        continue;
      }

      dialog.setStatusMessage(`Submitting restore for ${placeholder}…`);
      try {
        const response = await client.startRestore({
          siteUrl, webUrl,
          placeholderServerRelativeUrl: placeholder,
          originalServerRelativeUrl: metadata.originalServerRelativeUrl,
          conflictBehavior: 'Fail',
        });
        const baseName = placeholder.substring(placeholder.lastIndexOf('/') + 1);
        dialog.addAcceptedJob(response.jobId, response, `Restore: ${baseName}`);
        submitted++;
      } catch (err) {
        skipped.push(`${placeholder}: ${this.describeError(err, 'submit failed')}`);
      }
    }

    if (submitted === 0) {
      dialog.showError(
        skipped.length > 0
          ? `No restores were submitted:\n${skipped.join('\n')}`
          : 'No restores were submitted.',
      );
    } else if (skipped.length > 0) {
      // Continue polling the submitted jobs but inform the user about the
      // skipped items via the status banner.
      dialog.setStatusMessage(`Submitted ${submitted} restore${submitted === 1 ? '' : 's'}; skipped ${skipped.length}: ${skipped.join('; ')}`);
    }
  }

  // ---- Helpers ----

  private openDialog(operation: 'Migrate' | 'Restore'): MigrationProgressDialog {
    // Close any previous dialog so we never leak overlays / timers.
    this.activeDialog?.close();
    const dialog = new MigrationProgressDialog(this.apiClient!, operation);
    dialog.open(operation === 'Migrate' ? 'Preparing migration…' : 'Preparing restore…');
    this.activeDialog = dialog;
    return dialog;
  }

  private describeError(err: unknown, prefix: string): string {
    if (err instanceof ColdStorageApiError) {
      if (err.isUnauthorized) {
        return `${prefix}: you are not signed in or do not have permission (HTTP ${err.status}). Please refresh and sign in again.`;
      }
      if (err.isThrottled) {
        return `${prefix}: the server is throttling requests (HTTP 429). Please try again in a moment.`;
      }
      if (err.isServerError) {
        return `${prefix}: server error HTTP ${err.status}. ${err.bodyText ? this.truncate(err.bodyText, 240) : err.statusText}`;
      }
      if (err.isTransport) {
        return `${prefix}: ${err.message}`;
      }
      return `${prefix}: HTTP ${err.status} — ${err.bodyText ? this.truncate(err.bodyText, 240) : err.statusText}`;
    }
    if (err instanceof Error) {
      return `${prefix}: ${err.message}`;
    }
    return `${prefix}: ${String(err)}`;
  }

  private truncate(s: string, max: number): string {
    return s.length > max ? `${s.substring(0, max - 1)}…` : s;
  }

  private static tryParseIsoDate(value: unknown): string | undefined {
    if (value === null || value === undefined || value === '') return undefined;
    const s = typeof value === 'string' ? value : String(value);
    const d = new Date(s);
    return isNaN(d.getTime()) ? undefined : d.toISOString();
  }
}
