namespace AeroScience.Desktop;

// Resource ownership is local to each existing window; no file lookup or network access.
public class BrandedForm:Form
{
    public BrandedForm()
    {
        using var stream=typeof(BrandedForm).Assembly.GetManifestResourceStream("AeroScience.Branding.Icon")
            ??throw new InvalidOperationException("Packaged AeroScience icon resource is missing.");
        using var source=new Icon(stream);var owned=(Icon)source.Clone();Icon=owned;
        Disposed+=(_,_)=>owned.Dispose();
    }
    public static PictureBox PrimaryMark()
    {
        using var stream=typeof(BrandedForm).Assembly.GetManifestResourceStream("AeroScience.Branding.Primary")
            ??throw new InvalidOperationException("Packaged AeroScience branding resource is missing.");
        using var source=Image.FromStream(stream);var owned=new Bitmap(source);
        var image=new PictureBox{Image=owned,Size=new(48,48),SizeMode=PictureBoxSizeMode.Zoom,AccessibleName="AeroScience Orbital Arc logo",TabStop=false};
        image.Disposed+=(_,_)=>owned.Dispose();return image;
    }
}
