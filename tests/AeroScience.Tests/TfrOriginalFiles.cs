namespace AeroScience.Tests;

// Recorded public FAA packets, never a live/current NOTAM acceptance claim.
public static class TfrOriginalFiles
{
    public static byte[] Read(string name)=>File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"TfrOriginals",name+".original"));
    public static byte[] Packet(string id,bool aixm=false)=>Read(id=="6/2070"?"notice-"+(aixm?"aixm":"xml"):(aixm?"aixm-":"xml-")+id.Replace('/','_'));
}
