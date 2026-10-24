using AeroScience.Missions;

namespace AeroScience.Engine;

public interface IDesktopActions
{
    StorageStatus MissionStorage { get; }
    StorageStatus ScienceStorage { get; }
    ToolbarVersionStatus Toolbar { get; }
    void OpenToolbar();
    void OpenSettings();
    void OpenStorage();
    void OpenDataManagement();
    void CollectFeedback();
    void ImportScientificSource();
    void ChooseHyperspectral(bool folder);
    void ChooseEmitOriginals()=>throw new NotSupportedException("Choose EMIT files on the Windows companion PC.");
    void ChooseScientificDataset(string kind);
    void CompactStorage(bool science);
    void ConnectEarthdata()=>throw new NotSupportedException("Connect Earthdata from the current Windows desktop companion.");
}
