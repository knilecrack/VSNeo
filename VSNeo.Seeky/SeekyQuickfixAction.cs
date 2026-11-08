namespace VSNeo.Seeky;

internal static class SeekyQuickfixAction
{
    internal const string Replace = "replace";
    internal const string Append = "append";
    internal const string NewList = "new";

    internal static string Parse(string? value) => value switch
    {
        Append => Append,
        NewList => NewList,
        _ => Replace,
    };
}
