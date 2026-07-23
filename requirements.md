# SharePoint Online Cold Storage Requirements
We have a storage solution ([https://github.com/sambetts/spo-cold-storage](https://github.com/sambetts/spo-cold-storage)) that needs to be updated for new requirements.
Specifically, that files can be migrated from a SharePoint library by users that have permissions to do so, those files are replaced with a URL placeholder once confirmed, and files can be restored from cold storage back into SharePoint (also only by users with the right permissions).
More information below.
## Current Functionality
Currently we have an engine for queuing up files to be moved from SharePoint Online to an Azure storage container via Azure Service Bus.
There are two projects: Migration.Indexer and Migration.Migrator. Indexer finds content that matches a specific configuration (site/doc-lib) and then adds it to a queue for the migrator to move, when run. The indexer must be run manually right now.
Then we have a web interface to visualise content in the storage container, see migration logs, and use an Azure search to find content in the storage container.
## Functional Requirements for New System
We need a new SharePoint framework application customizer that injects a menu into SharePoint document libraries that can either queue the folder or file for migration to cold storage or restore a previously migrated item from cold storage back into SharePoint.
Cold storage should be configured for multiple destination containers so that each one can be given unique access permissions. Currently everything goes into a single container.
### SPFx component
The menu should show for anyone with contributor (edit) rights on the site and should be configurable with a backend URL to post both “start migrate” and “start restore” messages to. Important: cold storage actions are offered to users with at least contributor rights (which includes site owners).
It should confirm first with the user whether they want to move the selected items to cold storage or restore a selected placeholder-backed item from cold storage, and then let them select a destination container where relevant.
If a user has permission to migrate files, then we should also show the migration status in an extra column and the SPFx component should update this automatically.
### Web
The web project will duplicate somewhat the role of “Migration.Indexer” but for individual files and folders, by providing REST endpoints to enqueue both migration and restore requests. We’ll keep the indexer for scheduled runs of configured destinations.
We’ll also need a way to configure the blob storage containers and who has access to which container (by Entra ID group and user), while ensuring that only users with contributor (edit) rights on the site can trigger cold storage actions from SharePoint.
The web files browser needs to allow users to pick which container they want to browse. Is it possible to have ACLs in az storage by Entra ID UPN?
The web and API layers should also support looking up the target metadata behind a .url placeholder so that a restore request can recreate the original file in SharePoint in the correct library and path.
### Migrator
Right now, the migrator just copies files enqueued from SharePoint to a single Azure blob storage container.
It needs to also delete the file once confirmed as migrated, then replace it with a “.url” file of the same name that points to the new file in Azure storage and allow that placeholder to be used later to restore the item from cold storage back into SharePoint.
This new file should also have the same permissions (if it has broken inheritance) as the file it replaced.
When restoring, the system should use the placeholder metadata to recreate the original file in SharePoint at the original location where possible, remove or update the placeholder appropriately after a successful restore, and preserve the expected permissions on the restored item.
### Miscellaneous
Everything should be logged. There is a log in the SQL database currently; use that & adapt if necessary. The objective is to be able to see all steps for any given site/doc library – successes and errors.
Important: if there are any errors copying any file(s) to az storage, the source absolutely must not be deleted.
Scale expectations: this should be able to handle terabytes of data. The existing system has proven itself to be capable of batching lots of data without major performance issues, but this new system will also need to be performance tested. Include a test project to be able to test at scale individual classes & processes but also the whole system.
## Edge Cases
- The source file or folder must not be deleted if upload to Azure storage fails, if post-copy validation fails, or if placeholder creation fails.
- Duplicate migration or restore requests for the same item should be detected and handled idempotently.
- Folders should preserve their structure in cold storage, including nested content where recursive migration is requested.
- Locked or checked-out files must be handled gracefully and logged clearly.
- Items with broken inheritance must retain equivalent permissions on the replacement .url file and on the restored item where applicable.
- Already migrated items should be detected so they are not migrated again accidentally, and already restored items should not be restored again unexpectedly.
- Large files, unsupported file types, and items with invalid names or paths should be validated before migration or restore begins.
- Partial failures within a batch should be logged per item without causing successful items to be rolled back unnecessarily.
- The migration status shown in SharePoint should stay consistent with backend job state, including retry and failure states.
- If destination container access rules change after migration, browsing and access behaviour should remain predictable and auditable.
- If the original SharePoint location no longer exists during restore, the system should fail clearly or support a defined fallback location.
- If a conflicting file already exists at the restore destination, the restore behaviour should be defined clearly, for example fail, overwrite, or create a renamed copy.
- If placeholder metadata is incomplete or corrupted, the restore action should fail safely and log enough detail for investigation.
# Technical Implementation
Some suggested principals for the new functionality.
## Migration Lifecycle / Status Values
The system should track each migration or restore request through a consistent lifecycle so that SharePoint, the web application, and backend logs all reflect the same current state for each item or batch.
- Queued – the request has been accepted and added to Azure Service Bus.
- Validating – the item, path, placeholder metadata, permissions, and destination container are being checked before migration or restore starts.
- Validation Failed – pre-operation checks failed and the item was not migrated or restored.
- Migration In Progress – the migrator is actively copying the item from SharePoint to Azure storage.
- Copied to Cold Storage – the item has been copied to Azure storage but post-copy steps are not yet complete.
- Copy to Cold Storage Failed – the copy operation failed and the source item remains unchanged.
- Post-Copy Validation – the system is confirming that the copied item is accessible and complete in the destination.
- Delete Pending – the copy has been confirmed, and the source item is ready to be deleted.
- Delete Failed – the source item could not be deleted after a successful copy, so manual review or retry may be required.
- Placeholder Creating – the replacement .url file is being created in SharePoint.
- Placeholder Failed – the replacement .url file could not be created or permissioned correctly.
- Cold Storage Migration Completed – the item has been copied, the source has been deleted, and the replacement .url file has been created successfully.
- Restore In Progress – the system is copying the cold-stored item back into SharePoint.
- Restored to SharePoint – the file content has been recreated in SharePoint but post-restore steps are not yet complete.
- Restore Failed – the restore operation failed and the placeholder remains in place unless explicitly updated for error handling.
- Post-Restore Validation – the system is confirming that the restored item is accessible, complete, and in the correct location in SharePoint.
- Placeholder Removing – the system is removing or updating the .url placeholder after a successful restore.
- Placeholder Remove Failed – the item was restored but the placeholder could not be removed or updated automatically.
- Restore Completed – the item has been restored to SharePoint successfully and placeholder handling is complete.
- Completed with Warning – the migration or restore succeeded but a non-blocking issue was logged and should be reviewed.
- Retry Scheduled – the system has marked the item for another attempt.
- Cancelled – the request was cancelled before completion.
## SPFx to Web API Authentication
The preferred authentication model should use an Entra ID-secured web API together with SharePoint Framework single sign-on (SSO), so the SPFx component can call the API in the context of the current user without requiring an extra sign-in prompt. In practice, this should use the SPFx AadHttpClient pattern against an API application registration that exposes delegated permissions and is approved for the tenant. This allows the backend to receive the user identity from the Entra ID access token and apply authorization rules consistently using the delegated, on-behalf-of user context.
The solution should include the necessary API permission request in the SharePoint Framework package, and a SharePoint or global administrator will need to grant admin consent for those delegated permissions in the tenant before the component can call the API. The web API should validate Entra ID bearer access tokens, trust the delegated user context from the SSO flow, and then enforce its own application rules such as allowing only users with contributor (edit) rights on the site to start migration or restore operations. Where the backend needs to record the requesting user, it should use the identity from the validated token rather than trusting values passed in the request body.
If later requirements need app-only background behaviour, that should be handled separately in backend services rather than in the SPFx client. For interactive SharePoint actions, the design assumption should be that SPFx uses delegated user context with single sign-on (SSO) and AadHttpClient, while background workers continue to use their own managed identity or service principal where appropriate.
## REST API Contract
The web application should expose REST endpoints for migration, restore, status lookup, container discovery, and placeholder metadata lookup. The exact route names can be adjusted, but the API should keep the responsibilities below clear and stable so the SPFx component, web UI, and background workers can evolve independently.
### POST /api/migrations/start
Starts a migration request for one or more selected SharePoint files or folders. The request should include the SharePoint site identifier, library identifier, item identifiers or paths, the selected destination container, the requesting user, and whether folder recursion is required. The response should return an accepted result with a job identifier, the initial status, and any validation warnings that do not block queueing.
### POST /api/restores/start
Starts a restore request for a previously migrated item represented by a .url placeholder. The request should include the SharePoint site identifier, library identifier, placeholder item identifier or path, the requesting user, and any restore options such as conflict behavior or fallback destination if the original location no longer exists. The response should return an accepted result with a job identifier, the initial status, and any validation warnings.
### GET /api/jobs/{jobId}
Returns the current status for a migration or restore job, together with the item-level statuses, timestamps, selected destination container where relevant, and a summary of warnings or errors. This endpoint should support the SPFx status column, the web UI, and troubleshooting scenarios.
### GET /api/containers
Returns the list of configured cold storage containers that the current user is allowed to see or choose, together with display names and any metadata needed by the web UI or SPFx component. This endpoint should enforce container access rules and avoid returning containers the user should not see.
### GET /api/placeholders/resolve
Looks up the metadata behind a .url placeholder so the system can determine whether the item has been migrated, which container and blob it points to, the original SharePoint location, and whether it is eligible for restore. The request can identify the placeholder by SharePoint item identifier or path. The response should provide enough metadata for restore workflows without exposing unnecessary storage details to callers that do not need them.
### GET /api/jobs/{jobId}/logs
Returns the detailed processing log for a migration or restore job, including step-by-step events, warnings, and errors from the SQL-backed logging system. This endpoint is optional if equivalent functionality is already provided elsewhere in the web application, but the contract should exist somewhere in the system.
