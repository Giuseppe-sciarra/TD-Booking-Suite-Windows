namespace TD.Server.Web;

/// <summary>The public addresses of today, for the clients and the WordPress site: /book/, /action/, /api/wordpress/.</summary>
public static partial class Public
{
    public static void Map(WebApplication app)
    {
        PublicHost.Map(app);
        PublicPages.Map(app);
        MapBooking(app);
        MapActions(app);
        MapWordPress(app);
    }
    static partial void MapBooking(WebApplication app);
    static partial void MapActions(WebApplication app);
    static partial void MapWordPress(WebApplication app);
}
