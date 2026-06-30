import { Log } from '@microsoft/sp-core-library';
import {
  BaseFieldCustomizer,
  IFieldCustomizerCellEventParameters,
} from '@microsoft/sp-listview-extensibility';

import { colorFor, formatLabel, normalizeStatus } from '../../common/statusFormat';

const LOG_SOURCE = 'ColdStorageStatusFieldCustomizer';

/**
 * Renders a cold-storage badge for the lifecycle status column. As well as the
 * coloured status pill (when the column holds a recognized status), it marks any
 * ".url" placeholder row with a "❄ Cold storage" indicator so archived files are
 * visually distinguishable at a glance in the library view (issue #14).
 *
 * Note: SharePoint doesn't support swapping the native file-type icon, so a
 * field-customizer badge is the supported approach.
 */
export default class ColdStorageStatusFieldCustomizer extends BaseFieldCustomizer<{}> {
  public onInit(): Promise<void> {
    Log.info(LOG_SOURCE, 'Field customizer ready.');
    return Promise.resolve();
  }

  public onRenderCell(event: IFieldCustomizerCellEventParameters): void {
    if (!event.domElement) {
      return;
    }
    event.domElement.innerHTML = '';

    const value = event.fieldValue as string | number | undefined;
    const status = normalizeStatus(value);

    // Detect a cold-storage placeholder by its ".url" file name so an archived
    // file is distinguishable even before/without a status value. `row` may not
    // be present in every SPFx host, so read it defensively.
    const row = (event as unknown as { row?: { getValueByName(name: string): unknown } }).row;
    const leaf = row?.getValueByName('FileLeafRef');
    const isPlaceholder = typeof leaf === 'string' && leaf.toLowerCase().endsWith('.url');

    if (!status && !isPlaceholder) {
      // Ordinary file with no cold-storage status — render nothing rather than a
      // noisy "Unknown" pill.
      return;
    }

    const badge = document.createElement('span');
    if (status) {
      badge.textContent = `❄ ${formatLabel(value)}`;
      badge.style.background = colorFor(value);
    } else {
      badge.textContent = '❄ Cold storage';
      badge.style.background = '#0078d4';
    }
    if (isPlaceholder) {
      badge.title = 'This file is archived in cold storage.';
    }
    badge.style.display = 'inline-block';
    badge.style.padding = '2px 8px';
    badge.style.borderRadius = '12px';
    badge.style.fontSize = '12px';
    badge.style.color = '#fff';
    badge.style.whiteSpace = 'nowrap';
    event.domElement.appendChild(badge);
  }

  public onDisposeCell(event: IFieldCustomizerCellEventParameters): void {
    if (event.domElement) {
      event.domElement.innerHTML = '';
    }
  }
}
