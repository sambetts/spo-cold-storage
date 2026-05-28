# SPO Cold Storage – SharePoint Framework solution

This solution adds two extensions to SharePoint Online document libraries:

| Extension | Type | Purpose |
| --- | --- | --- |
| `ColdStorageCommandSet` | ListView Command Set | "Migrate to cold storage" / "Restore from cold storage" buttons on the library toolbar. |
| `ColdStorageStatusFieldCustomizer` | Field Customizer | Renders a colored badge for the `ColdStorageStatus` site column. |

The extensions call the SPO Cold Storage web API (`src/Web/Web.Server`) via
`AadHttpClient` so the backend can enforce the site-collection-owner check and
the per-container ACLs.

## Prerequisites

* Node 18 LTS
* SharePoint Framework toolchain (`@microsoft/generator-sharepoint` 1.19)
* The Azure AD app for the cold-storage web API must expose
  `user_impersonation`; the SPFx solution requests that scope via
  `webApiPermissionRequests` in `package-solution.json`.

## Configure

Edit `package-solution.json` and replace the placeholder GUIDs (`solution.id`,
feature ID) with your tenant's values. Set the API base URL when registering
the extension custom action, e.g.:

```json
{
  "ClientSideComponentProperties": "{\"apiBaseUrl\":\"https://cold-storage.contoso.com\",\"apiAppIdUri\":\"api://cold-storage.contoso.com\"}"
}
```

## Build & package

```cmd
cd src\SPFx\spfx-cold-storage
npm install
gulp bundle --ship
gulp package-solution --ship
```

The resulting `.sppkg` is written to `sharepoint/solution/`. Upload to the
tenant App Catalog and approve the API permission request from the
SharePoint Admin Center.

## Field provisioning

The `sharepoint/assets/elements.xml` element manifest creates a `ColdStorageStatus`
site column wired to the `ColdStorageStatusFieldCustomizer`. Add the column to
any document library where you want the lifecycle status to show.

## Notes

* The "Restore" command is only visible when every selected item is a
  `.url` placeholder file.
* The web API decides whether a given item is eligible for restore (status =
  `ColdStorageMigrationCompleted`, container ACL grants `canRestore`); the
  command set shows the API's error message on rejection.
* No assets are embedded in the bundle for icons; supply your own under
  `sharepoint/assets/icons/`.
