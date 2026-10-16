namespace AzlDl.App;

// Where help and problem reports go: the project's repository, its guide and its issue tracker. Reports open as a blank
// issue; nothing from the user's machine (paths, error text) is put into the link.
public static class Links
{
    public static readonly Uri Repository = new("https://github.com/vermilion10/azl-dl");

    public static readonly Uri Guide = new("https://github.com/vermilion10/azl-dl#readme");

    public static readonly Uri ReportProblem = new("https://github.com/vermilion10/azl-dl/issues/new");
}
