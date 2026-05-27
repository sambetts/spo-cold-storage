interface SPListResponse {
    d: SPListResponseData
}
interface SPListResponseData {
    results: SPList[]
}
interface SPList {
    Title: string,
    Description: string,
    Id: string,
    Hidden: boolean,
    NoCrawl: boolean
}



interface SPFolderResponse {
    d: SPFolderResponseData
}
interface SPFolderResponseData {
    results: SPFolder[]
}
interface SPFolder {
    Name: string,
    ServerRelativeUrl: string,
    ItemCount: number
}

interface SPAuthInfo
{
    bearer: string,
    digest: string
}

export {
    SPListResponse, SPList, SPFolder, SPFolderResponse, SPAuthInfo
  }