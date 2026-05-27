
DECLARE @blockGuid AS UNIQUEIDENTIFIER;
--[blockset]--
--The above comment will be replaced with SET @blockGuid='blockGuid';

INSERT INTO sites(url)
	SELECT distinct imports.SiteUrl 
	FROM [StagingFiles] imports
	left join sites on sites.url = imports.SiteUrl
	left join sites duplicates on duplicates.url = imports.SiteUrl
	where sites.url is null and imports.SiteUrl is not null and imports.SiteUrl != ''
	and imports.ImportBlockId = @blockGuid
	and duplicates.url is null

INSERT INTO webs(url, site_id)
	SELECT distinct imports.WebUrl, sites.id
	FROM [StagingFiles] imports
	inner join sites on sites.url = imports.SiteUrl
	left join webs on webs.url = imports.WebUrl
	left join webs duplicates on duplicates.url = imports.WebUrl
	where webs.url is null and imports.SiteUrl is not null and imports.SiteUrl != ''
	and imports.ImportBlockId = @blockGuid
	and duplicates.url is null

INSERT INTO users(email)
	SELECT distinct imports.Author 
	FROM [StagingFiles] imports
	left join users on users.email = imports.Author
		left join users duplicates on duplicates.email = imports.Author
	where users.email is null
	and duplicates.email is null
	and imports.ImportBlockId = @blockGuid

-- Insert new directories from staging
INSERT INTO file_directories(directory_path)
	SELECT distinct imports.DirectoryPath 
	FROM [StagingFiles] imports
	left join file_directories on file_directories.directory_path = imports.DirectoryPath
	left join file_directories duplicates on duplicates.directory_path = imports.DirectoryPath
	where file_directories.directory_path is null
	and duplicates.directory_path is null
	and imports.DirectoryPath is not null
	and imports.DirectoryPath != ''
	and imports.ImportBlockId = @blockGuid

-- Insert only new files
insert into files(
	url,
	last_modified,
	created_date,
	last_modified_by_user_id,
	web_id,
	directory_id,
	file_size,
	version_count,
	versions_total_size
	)
	select distinct 
		imports.ServerRelativeFilePath, 
		imports.LastModified,
		imports.CreatedDate,
		users.id as userId,
		webs.id as webId,
		file_directories.id as directoryId,
		imports.FileSize,
		0 as version_count,
		0 as versions_total_size
	FROM [StagingFiles] imports
		inner join users on users.email = imports.Author
		inner join webs on webs.url = imports.WebUrl
		left join file_directories on file_directories.directory_path = imports.DirectoryPath
	left join files duplicates on duplicates.url = imports.ServerRelativeFilePath
	where duplicates.url is null and
	imports.ImportBlockId = @blockGuid

delete from [StagingFiles] where ImportBlockId = @blockGuid
