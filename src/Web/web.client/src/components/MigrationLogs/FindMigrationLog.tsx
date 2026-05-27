import React from 'react';
import Button from '@mui/material/Button';
import TextField from '@mui/material/TextField';
import moment from 'moment';

interface SharePointFile {
    url: string;
    web: SharePointWeb;
    lastModified: Date;
}
interface SharePointWeb {
    url: string;
    lastModifiedBy : string;
}
interface MigrationLog {
    file: SharePointFile;
    migrated: Date;
}

interface SearchLogsState {
    searchLogs: Array<MigrationLog>,
    loading: boolean,
    searchTerm: string;
}
interface SearchLogsProps {
    token: string;
}

export class FindMigrationLog extends React.Component<SearchLogsProps, SearchLogsState> {

    constructor(props: SearchLogsProps) {
        super(props);
        this.state = { searchLogs: [], loading: false, searchTerm: "" };
    }

    componentDidMount() {
        if (this.state.searchTerm !== "") {
            this.populateSearchLogsFromSearch();
        }
    }

    async populateSearchLogsFromSearch() {
        if (this.state.searchTerm.length > 0) {
            this.setState({ loading: true });
            await fetch('migrationrecord?keyWord=' + this.state.searchTerm, {
                method: 'GET',
                headers: {
                  'Content-Type': 'application/json',
                  'Authorization': 'Bearer ' + this.props.token,
                }})
                .then(async response => {
                    const data = await response.json();
                    console.log(data);
                    this.setState({ searchLogs: data, loading: false });
                    this.setState({ loading: false });
                })
                .catch(err => {
                    alert('Loading data failed');
                    this.setState({ searchLogs: [], loading: false });
                });
        }
    }

    static renderResultsTable(logs: Array<MigrationLog>) {
        return (
            <div>
                {logs.length > 0 ?
                    <div>
                        <p>{logs.length} documents found.</p>
                        <table className='table table-striped' aria-labelledby="tabelLabel">
                            <thead>
                                <tr>
                                    <th>File name</th>
                                    <th>Web</th>
                                    <th>Last Modified</th>
                                    <th>Migrated</th>
                                </tr>
                            </thead>
                            <tbody>
                                {logs.map((log: MigrationLog) =>
                                    <tr key={log.file?.url}>
                                        <td>{log.file?.url.split('/').pop()}</td>
                                        <td>{log.file.web.url}</td>
                                        <td>{moment(log.file.lastModified).format('D-MMM-YYYY HH:mm')}</td>
                                        <td>{moment(log.migrated).format('D-MMM-YYYY HH:mm')}</td>
                                    </tr>
                                )}
                            </tbody>
                        </table>
                    </div>


                    :
                    <div>No files found</div>
                }

            </div>
        );
    }

    render() {
        let contents = this.state.loading
            ? <p><em>Loading...</em></p>
            : FindMigrationLog.renderResultsTable(this.state.searchLogs);

        return (
            <div>
                <h1 id="tabelLabel">Migration Logs</h1>
                <p>Search for a file that's been <strong>succesfully</strong> migrated to cold-storage. These results look for migration logs in the SQL database only, not the file iteself in blob storage.</p>
                <TextField id="outlined-basic" label="Search term" variant="standard" required
                    onChange={e => { this.setState({ searchTerm: e.target.value }); }} />
                <Button variant="outlined" size="large"
                    onClick={() => {
                        this.populateSearchLogsFromSearch();
                    }}
                >Search</Button>

                {contents}
            </div>
        );
    }

}