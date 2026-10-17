using AeroScience.Missions;

namespace AeroScience.Engine;

public interface IDesktopActions
{
    StorageStatus MissionStorage { get; }
    StorageStatus ScienceStorage { get; }
    void OpenSettings();
    void OpenStorage();
    void OpenDataManagement();
    void CollectFeedback();
}
