import React from 'react';
import { ListFolderConfig, SiteListFilterConfig } from '../TargetSitesInterfaces';
import { SimpleTreeView } from '@mui/x-tree-view/SimpleTreeView';
import { ListAndFolders } from "./ListAndFolders";
import ExpandMoreIcon from '@mui/icons-material/ExpandMore';
import ChevronRightIcon from '@mui/icons-material/ChevronRight';
import { SPAuthInfo, SPList, SPListResponse } from './SPDefs';

interface Props {
    spoAuthInfo: SPAuthInfo,
    targetSite: SiteListFilterConfig,
    folderRemoved: Function,
    folderAdd: Function,
    listRemoved: Function,
    listAdd: Function
}

export const SiteListsAndLibraries: React.FC<Props> = (props) => {
    const [spLists, setSpLists] = React.useState<SPList[] | null>(null);
    const [targetSite, setTargetSite] = React.useState<SiteListFilterConfig>();

    React.useEffect(() => {
        setTargetSite(props.targetSite);
    }, [props.targetSite]);

    const getFilterConfigForSPList = React.useCallback((list: SPList): ListFolderConfig | null => {

        // Find config from existing list
        let listInfo : ListFolderConfig | null = null;
        if (targetSite!.listFilterConfig && targetSite!.listFilterConfig) {
            targetSite!.listFilterConfig!.forEach((l: ListFolderConfig) => {
                if (l.listTitle === list.Title) {
                    listInfo = l;
                }
            });
        }
        

        return listInfo;
    }, [targetSite]);

    

    React.useEffect(() => {

        // Load SharePoint lists from SPO REST
        fetch(`${props.targetSite.rootURL}/_api/web/lists`, {
            method: 'GET',
            headers: {
                'Content-Type': 'application/json',
                Accept: "application/json;odata=verbose",
                'Authorization': 'Bearer ' + props.spoAuthInfo.bearer,
            }
        }
        )
            .then(async response => {

                if (!response.ok) {
                    const errorText = await response.text();
                    console.error('SharePoint API error:', response.status, errorText);
                    alert(`Error loading SharePoint lists: ${response.status} - ${response.statusText}`);
                    setSpLists([]);
                    return;
                }

                var responseText = await response.text();
                const data: SPListResponse = JSON.parse(responseText);

                if (data.d?.results) {

                    const lists: SPList[] = data.d.results;
                    console.log('Loaded SharePoint lists:', lists);
                    
                    // Filter out hidden system lists
                    const visibleLists = lists.filter(list => !list.Hidden);
                    console.log('Visible lists after filtering:', visibleLists);
                    
                    setSpLists(visibleLists);
                }
                else {
                    console.error('Unexpected response format from SharePoint:', responseText);
                    alert('Unexpected response from SharePoint for lists: ' + responseText);
                    setSpLists([]);
                }
            })
            .catch(err => {
                console.error('Error fetching SharePoint lists:', err);
                alert('Failed to load SharePoint lists: ' + err.message);
                setSpLists([]);
            });
    }, [props.spoAuthInfo, props.targetSite, getFilterConfigForSPList]);

    
    const folderRemoved = (folder : string, list : ListFolderConfig) => {
        props.folderRemoved(folder, list, props.targetSite);
    }
    const folderAdd = (folder : string, list : ListFolderConfig) => {
        props.folderAdd(folder, list, props.targetSite);
    }

    const listRemoved = (list : string) => {
        props.listRemoved(list, props.targetSite);
    }
    const listAdd = (listName : string) => {
        props.listAdd(listName, props.targetSite);
    }


    return (
        <div>
            {spLists === null ?
                (
                    <div>Loading...</div>
                )
                :
                spLists.length === 0 ?
                (
                    <div style={{ padding: '20px' }}>
                        <p>No lists or libraries found in this site.</p>
                        <p>Please check the browser console for errors.</p>
                    </div>
                )
                :
                (
                    <div style={{ padding: '20px' }}>
                        <h3>Lists and Libraries ({spLists.length} found)</h3>
                        <SimpleTreeView
                            slots={{
                                collapseIcon: ExpandMoreIcon,
                                expandIcon: ChevronRightIcon,
                            }}
                        >
                            {spLists.map((splist: SPList) => {
                                console.log('Rendering list:', splist.Title);
                                return (
                                    <ListAndFolders spoAuthInfo={props.spoAuthInfo} list={splist} targetListConfig={getFilterConfigForSPList(splist)} 
                                        folderAdd={(f : string, list : ListFolderConfig)=> folderAdd(f, list)}
                                        folderRemoved={(f : string, list : ListFolderConfig)=> folderRemoved(f, list)}
                                        listAdd={() => listAdd(splist.Title)} listRemoved={() => listRemoved(splist.Title)}
                                        key={splist.Title}
                                    />
                                );
                            })}
                        </SimpleTreeView>
                    </div>
                )
            }
        </div>
    );
}
