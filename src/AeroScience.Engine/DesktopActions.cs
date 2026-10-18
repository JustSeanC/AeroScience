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
    void CompactStorage(bool science);
}
