import { Log } from '@microsoft/sp-core-library';
import { BaseListViewCommandSet, Command, IListViewCommandSetExecuteEventParameters, ListViewStateChangedEventArgs } from '@microsoft/sp-listview-extensibility';
import { AadHttpClient } from '@microsoft/sp-http';

import { ColdStorageApiClient, IStartMigrationItem } from '../../common/ColdStorageApiClient';

export interface IColdStorageCommandSetProperties {
  apiBaseUrl: string;
  apiAppIdUri: string;
}

const LOG_SOURCE = 'ColdStorageCommandSet';

/**
 * ListView Command Set that adds Migrate/Restore commands to a document
 * library toolbar. The web API enforces the site-collection-owner check, so
 * here we just hide the commands when the user has nothing selected.
 */
export default class ColdStorageCommandSet extends BaseListViewCommandSet<IColdStorageCommandSetProperties> {
  private apiClient?: ColdStorageApiClient;

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

  public onListViewUpdated(event: ListViewStateChangedEventArgs): void {
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

  public async onExecute(event: IListViewCommandSetExecuteEventParameters): Promise<void> {
    if (!this.apiClient) {
      return;
    }
    const siteUrl = this.context.pageContext.site.absoluteUrl;
    const webUrl = this.context.pageContext.web.absoluteUrl;

    if (event.itemId === 'COLDSTORAGE_MIGRATE') {
      const containers = await this.apiClient.listContainers();
      const target = containers.find(c => c.canMigrate);
      if (!target) {
        alert('You do not have permission to migrate to any configured cold-storage container.');
        return;
      }
      const items: IStartMigrationItem[] = event.selectedRows.map(row => ({
        serverRelativeUrl: row.getValueByName('FileRef') as string,
        itemKind: (row.getValueByName('FSObjType') as string) === '1' ? 'Folder' : 'File',
        fileSize: Number(row.getValueByName('File_x0020_Size')) || undefined,
        lastModified: row.getValueByName('Modified') as string,
      }));
      const response = await this.apiClient.startMigration({
        siteUrl,
        webUrl,
        containerName: target.name,
        recursive: true,
        items,
      });
      alert(`Migration accepted. Job id: ${response.jobId}\nStatus: ${response.status}`);
    } else if (event.itemId === 'COLDSTORAGE_RESTORE') {
      for (const row of event.selectedRows) {
        const placeholder = row.getValueByName('FileRef') as string;
        const metadata = await this.apiClient.resolvePlaceholder(placeholder);
        if (!metadata.isEligibleForRestore) {
          alert(`'${placeholder}' is not eligible for restore: ${metadata.unavailableReason ?? 'unknown reason'}.`);
          continue;
        }
        const response = await this.apiClient.startRestore({
          siteUrl,
          webUrl,
          placeholderServerRelativeUrl: placeholder,
          originalServerRelativeUrl: metadata.originalServerRelativeUrl,
          conflictBehavior: 'Fail',
        });
        alert(`Restore accepted. Job id: ${response.jobId}\nStatus: ${response.status}`);
      }
    }
  }
}
