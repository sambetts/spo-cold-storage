import { BlobItem, ContainerClient } from '@azure/storage-blob';
import React from 'react';
import { Component } from 'react';
import { StorageInfo } from '../ConfigReader'
import './FileExplorer.css';

interface FileListProps {
    navToFolderCallback?: Function;
    accessToken: string,
    client: ContainerClient,
    storageInfo: StorageInfo
}
interface FileListState {
    blobItems: BlobItem[] | null,
    currentDirs: string[] | null,
    storagePrefix: string
}

export class BlobFileList extends Component<FileListProps, FileListState> {

    constructor(props: FileListProps)
    {
        super(props);
        this.state = { blobItems: null, currentDirs: null, storagePrefix: ""};
    }

    componentDidMount()
    {
        if (this.props.client) {
            this.listFiles("");
        }
    }

    setDir(clickedPrefix: string) {
        this.setState({storagePrefix: clickedPrefix});
        if (this.props.navToFolderCallback) {
            this.props.navToFolderCallback(clickedPrefix);
        }

        this.listFiles(clickedPrefix);
    }

    getDirName(fullName: string): string {
        const dirs = fullName.split("/");
        return dirs[dirs.length - 2];
    }
    getFileName(fullName: string): string {
        const dirs = fullName.split("/");
        return dirs[dirs.length - 1];
    }

    breadcrumbDirClick(dirIdx: number, allDirs: string[]) {
        let fullPath: string = "";

        for (let index = 0; index <= dirIdx; index++) {
            const thisDir = allDirs[index];
            fullPath += `${thisDir}/`;
        }
        this.setNewPath(fullPath);
    }
    setNewPath(newPath: string) {
        this.setState({storagePrefix: newPath});
        this.listFiles(newPath);
    }

    async listFiles(prefix: string) {

        let dirs: string[] = [];
        let blobs: BlobItem[] = [];

        try {
            let iter = this.props.client.listBlobsByHierarchy("/", { prefix: prefix });

            for await (const item of iter) {
                if (item.kind === "prefix") {
                    dirs.push(item.name);
                } else {
                    blobs.push(item);
                }
            }

            this.setState({ blobItems: blobs, currentDirs: dirs });

            return Promise.resolve();
        } catch (error) {
            return Promise.reject(error);
        }
    }

    getUrl(fileName: String) 
    {
        return this.props.storageInfo.accountURI + this.props.storageInfo.containerName + "/" + fileName 
            + this.props.storageInfo.sharedAccessToken;
    }

    formatFileSize(bytes: number): string {
        if (bytes === 0) return '0 Bytes';
        const k = 1024;
        const sizes = ['Bytes', 'KB', 'MB', 'GB', 'TB'];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        return Math.round((bytes / Math.pow(k, i)) * 100) / 100 + ' ' + sizes[i];
    }

    formatDate(date: Date | undefined): string {
        if (!date) return '-';
        return new Intl.DateTimeFormat('en-US', {
            year: 'numeric',
            month: 'short',
            day: 'numeric',
            hour: '2-digit',
            minute: '2-digit'
        }).format(new Date(date));
    }

    render() {
        const breadcumbDirs = this.state.storagePrefix.split("/").filter(d => d);
        const hasItems = (this.state.currentDirs && this.state.currentDirs.length > 0) || 
                        (this.state.blobItems && this.state.blobItems.length > 0);

        return (
            <div className="file-explorer">
                {/* Breadcrumb Navigation */}
                <div className="breadcrumb-bar">
                    <div className="breadcrumb-navigation">
                        <svg className="breadcrumb-icon" width="16" height="16" viewBox="0 0 16 16" fill="currentColor">
                            <path d="M8.707 1.5a1 1 0 0 0-1.414 0L.646 8.146a.5.5 0 0 0 .708.708L2 8.207V13.5A1.5 1.5 0 0 0 3.5 15h9a1.5 1.5 0 0 0 1.5-1.5V8.207l.646.647a.5.5 0 0 0 .708-.708L13 5.793V2.5a.5.5 0 0 0-.5-.5h-1a.5.5 0 0 0-.5.5v1.293L8.707 1.5ZM13 7.207V13.5a.5.5 0 0 1-.5.5h-9a.5.5 0 0 1-.5-.5V7.207l5-5 5 5Z"/>
                        </svg>
                        <button onClick={() => this.setNewPath("")} className="breadcrumb-link">
                            Root
                        </button>
                        {breadcumbDirs.map((breadcumbDir, dirIdx) => (
                            <React.Fragment key={dirIdx}>
                                <svg className="breadcrumb-separator" width="8" height="12" viewBox="0 0 8 12" fill="currentColor">
                                    <path d="M1 1l5 5-5 5"/>
                                </svg>
                                <button 
                                    onClick={() => this.breadcrumbDirClick(dirIdx, breadcumbDirs)} 
                                    className="breadcrumb-link"
                                >
                                    {breadcumbDir}
                                </button>
                            </React.Fragment>
                        ))}
                    </div>
                </div>

                {/* File List Header */}
                <div className="file-list-header">
                    <div className="file-list-column file-name-column">Name</div>
                    <div className="file-list-column file-modified-column">Date modified</div>
                    <div className="file-list-column file-size-column">Size</div>
                </div>

                {/* File List Content */}
                <div className="file-list-content">
                    {!hasItems && (
                        <div className="empty-folder">
                            <svg className="empty-folder-icon" width="48" height="48" viewBox="0 0 48 48" fill="currentColor">
                                <path d="M6 10a4 4 0 0 1 4-4h8.586a2 2 0 0 1 1.414.586l2.828 2.828A2 2 0 0 0 24.242 10H38a4 4 0 0 1 4 4v20a4 4 0 0 1-4 4H10a4 4 0 0 1-4-4V10z"/>
                            </svg>
                            <p className="empty-folder-text">This folder is empty</p>
                        </div>
                    )}

                    {/* Folders */}
                    {this.state.currentDirs && this.state.currentDirs.map(dir => (
                        <div key={dir} className="file-list-item folder-item" onClick={() => this.setDir(dir)}>
                            <div className="file-list-cell file-name-cell">
                                <svg className="file-icon folder-icon" width="20" height="20" viewBox="0 0 20 20" fill="currentColor">
                                    <path d="M2 4.5A1.5 1.5 0 0 1 3.5 3h4.879a1.5 1.5 0 0 1 1.06.44l1.122 1.12A1.5 1.5 0 0 0 11.621 5H16.5A1.5 1.5 0 0 1 18 6.5v9a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 2 15.5v-11z"/>
                                </svg>
                                <span className="file-name">{this.getDirName(dir)}</span>
                            </div>
                            <div className="file-list-cell file-modified-cell">-</div>
                            <div className="file-list-cell file-size-cell">-</div>
                        </div>
                    ))}

                    {/* Files */}
                    {this.state.blobItems && this.state.blobItems.map(blob => (
                        <div key={blob.name} className="file-list-item file-item">
                            <div className="file-list-cell file-name-cell">
                                <svg className="file-icon document-icon" width="20" height="20" viewBox="0 0 20 20" fill="currentColor">
                                    <path d="M4 3.5A1.5 1.5 0 0 1 5.5 2h5.086a1.5 1.5 0 0 1 1.06.44l3.915 3.914c.28.28.439.662.439 1.06V16.5A1.5 1.5 0 0 1 14.5 18h-9A1.5 1.5 0 0 1 4 16.5v-13zm7 0v3.5A1.5 1.5 0 0 0 12.5 8.5h3.5L11 3.5z"/>
                                </svg>
                                <a href={this.getUrl(blob.name)} className="file-name file-link" target="_blank" rel="noopener noreferrer">
                                    {this.getFileName(blob.name)}
                                </a>
                            </div>
                            <div className="file-list-cell file-modified-cell">
                                {this.formatDate(blob.properties.lastModified)}
                            </div>
                            <div className="file-list-cell file-size-cell">
                                {this.formatFileSize(blob.properties.contentLength || 0)}
                            </div>
                        </div>
                    ))}
                </div>
            </div>
        );
    }
}
