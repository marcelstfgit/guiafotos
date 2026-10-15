namespace GuiaFotos.Core;

public static class AppPaths
{
    public static string Workspace(string applicationDirectory, string localAppData)
        => File.Exists(Path.Combine(applicationDirectory, "portable.marker"))
            ? Path.Combine(applicationDirectory, "Data")
            : Path.Combine(localAppData, "GuiaFotos");
}
