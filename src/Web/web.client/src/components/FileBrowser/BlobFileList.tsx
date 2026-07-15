import React from 'react';
import { Component } from 'react';
import './FileExplorer.css';

interface FileListProps {
    accessToken: string;
    navToFolderCallback?: (prefix: string) => void;
}

interface FileEntry {
    name: string;
    size: number;
    lastModified?: string;
}

interface FileListState {
    files: FileEntry[] | null;
    currentDirs: string[] | null;
    storagePrefix: string;
    isLoading: boolean;
    listError: string | null;
    listErrorDetail: string | null;
}

// The browser can no longer reach Azure Storage directly (the account's public
// network access is disabled by policy), so listing and downloading go through
// our own API, which proxies to storage over the Web App's private endpoint.
export class BlobFileList extends Component<FileListProps, FileListState> {

    constructor(props: FileListProps) {
        super(props);
        this.state = {
            files: null,
            currentDirs: null,
            storagePrefix: "",
            isLoading: false,
            listError: null,
            listErrorDetail: null
        };
    }

    componentDidMount() {
        this.listFiles("").catch((err) => {
            console.error('Initial blob listing failed.', err);
        });
    }

    setDir(clickedPrefix: string) {
        this.setState({ storagePrefix: clickedPrefix });
        if (this.props.navToFolderCallback) {
            this.props.navToFolderCallback(clickedPrefix);
        }
        this.listFiles(clickedPrefix).catch((err) => {
            console.error('Folder navigation listing failed.', err);
        });
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
            fullPath += `${allDirs[index]}/`;
        }
        this.setNewPath(fullPath);
    }
    setNewPath(newPath: string) {
        this.setState({ storagePrefix: newPath });
        this.listFiles(newPath).catch((err) => {
            console.error('Breadcrumb navigation listing failed.', err);
        });
    }

    async listFiles(prefix: string) {
        this.setState({ isLoading: true, listError: null, listErrorDetail: null });

        try {
            const response = await fetch('/api/storage/blobs?prefix=' + encodeURIComponent(prefix), {
                headers: { 'Authorization': 'Bearer ' + this.props.accessToken }
            });

            if (!response.ok) {
                let body = '';
                try { body = await response.text(); } catch { /* ignore */ }
                throw new Error(`HTTP ${response.status} ${response.statusText}${body ? `: ${body}` : ''}`);
            }

            const data = await response.json();
            this.setState({
                files: (data.files ?? []) as FileEntry[],
                currentDirs: (data.folders ?? []) as string[],
                isLoading: false,
                listError: null,
                listErrorDetail: null
            });
        } catch (error: any) {
            console.error('Blob listing failed.', error);
            this.setState({
                isLoading: false,
                listError: 'The server could not list files in cold storage.',
                listErrorDetail: error?.message ?? String(error),
                files: [],
                currentDirs: []
            });
        }
    }

    async downloadFile(name: string) {
        try {
            const response = await fetch('/api/storage/download?blob=' + encodeURIComponent(name), {
                headers: { 'Authorization': 'Bearer ' + this.props.accessToken }
            });

            if (!response.ok) {
                let body = '';
                try { body = await response.text(); } catch { /* ignore */ }
                throw new Error(`HTTP ${response.status} ${response.statusText}${body ? `: ${body}` : ''}`);
            }

            const blob = await response.blob();
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = this.getFileName(name);
            document.body.appendChild(a);
            a.click();
            a.remove();
            URL.revokeObjectURL(url);
        } catch (error: any) {
            console.error('Download failed.', error);
            alert('Could not download the file: ' + (error?.message ?? String(error)));
        }
    }

    formatFileSize(bytes: number): string {
        if (bytes === 0) return '0 Bytes';
        const k = 1024;
        const sizes = ['Bytes', 'KB', 'MB', 'GB', 'TB'];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        return Math.round((bytes / Math.pow(k, i)) * 100) / 100 + ' ' + sizes[i];
    }

    formatDate(date: string | undefined): string {
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
                        (this.state.files && this.state.files.length > 0);

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
                    {this.state.listError && (
                        <div className="list-error" role="alert">
                            <strong>Could not list files.</strong>
                            <p>{this.state.listError}</p>
                            {this.state.listErrorDetail && (
                                <details className="error-details list-error-details">
                                    <summary>Technical details</summary>
                                    <pre>{this.state.listErrorDetail}</pre>
                                </details>
                            )}
                            <button
                                type="button"
                                className="error-retry-button"
                                onClick={() => this.listFiles(this.state.storagePrefix).catch(() => { /* handled in state */ })}
                            >
                                Retry
                            </button>
                        </div>
                    )}

                    {this.state.isLoading && !this.state.listError && (
                        <div className="loading-container small">
                            <div className="loading-spinner"></div>
                            <p>Loading files...</p>
                        </div>
                    )}

                    {!this.state.isLoading && !this.state.listError && !hasItems && (
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
                    {this.state.files && this.state.files.map(file => (
                        <div key={file.name} className="file-list-item file-item">
                            <div className="file-list-cell file-name-cell">
                                <svg className="file-icon document-icon" width="20" height="20" viewBox="0 0 20 20" fill="currentColor">
                                    <path d="M4 3.5A1.5 1.5 0 0 1 5.5 2h5.086a1.5 1.5 0 0 1 1.06.44l3.915 3.914c.28.28.439.662.439 1.06V16.5A1.5 1.5 0 0 1 14.5 18h-9A1.5 1.5 0 0 1 4 16.5v-13zm7 0v3.5A1.5 1.5 0 0 0 12.5 8.5h3.5L11 3.5z"/>
                                </svg>
                                <button
                                    type="button"
                                    className="file-name file-link"
                                    style={{ background: 'none', border: 'none', padding: 0, font: 'inherit', color: 'inherit', cursor: 'pointer', textAlign: 'left' }}
                                    onClick={() => this.downloadFile(file.name)}
                                    title="Download"
                                >
                                    {this.getFileName(file.name)}
                                </button>
                            </div>
                            <div className="file-list-cell file-modified-cell">
                                {this.formatDate(file.lastModified)}
                            </div>
                            <div className="file-list-cell file-size-cell">
                                {this.formatFileSize(file.size || 0)}
                            </div>
                        </div>
                    ))}
                </div>
            </div>
        );
    }
}
