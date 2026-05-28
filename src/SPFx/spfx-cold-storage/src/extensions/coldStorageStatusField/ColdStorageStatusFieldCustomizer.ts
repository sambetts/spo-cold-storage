import { Log } from '@microsoft/sp-core-library';
import {
  BaseFieldCustomizer,
  IFieldCustomizerCellEventParameters,
} from '@microsoft/sp-listview-extensibility';

const LOG_SOURCE = 'ColdStorageStatusFieldCustomizer';

/**
 * Renders a friendly badge for the cold-storage lifecycle status column. The
 * column itself is a Choice field that stores the raw enum name written by the
 * backend - the customizer translates the raw value into a color and label.
 */
export default class ColdStorageStatusFieldCustomizer extends BaseFieldCustomizer<{}> {
  public onInit(): Promise<void> {
    Log.info(LOG_SOURCE, 'Field customizer ready.');
    return Promise.resolve();
  }

  public onRenderCell(event: IFieldCustomizerCellEventParameters): void {
    const value: string = (event.fieldValue as string) ?? '';
    if (!event.domElement) {
      return;
    }
    const badge = document.createElement('span');
    badge.textContent = ColdStorageStatusFieldCustomizer.formatLabel(value);
    badge.style.display = 'inline-block';
    badge.style.padding = '2px 8px';
    badge.style.borderRadius = '12px';
    badge.style.fontSize = '12px';
    badge.style.background = ColdStorageStatusFieldCustomizer.colorFor(value);
    badge.style.color = '#fff';
    event.domElement.innerHTML = '';
    event.domElement.appendChild(badge);
  }

  public onDisposeCell(event: IFieldCustomizerCellEventParameters): void {
    if (event.domElement) {
      event.domElement.innerHTML = '';
    }
  }

  private static colorFor(value: string): string {
    if (!value) return '#888';
    if (value.endsWith('Failed') || value === 'Cancelled') return '#a4262c';
    if (value === 'CompletedWithWarning' || value === 'RetryScheduled' || value === 'PlaceholderRemoveFailed') return '#797775';
    if (value === 'ColdStorageMigrationCompleted' || value === 'RestoreCompleted') return '#107c10';
    return '#0078d4';
  }

  private static formatLabel(value: string): string {
    if (!value) return '';
    return value.replace(/([A-Z])/g, ' $1').replace(/^./, c => c.toUpperCase()).trim();
  }
}
