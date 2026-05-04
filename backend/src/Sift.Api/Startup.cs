using Microsoft.Extensions.DependencyInjection;
using Sift.Api.Infrastructure;
using Sift.Api.Services;

namespace Sift.Api;

public static class Startup
{
    public static IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        // DbConnectionFactory is a singleton — it caches Secrets Manager credentials
        // across warm Lambda invocations.
        services.AddSingleton<DbConnectionFactory>();

        services.AddTransient<IDocumentService, DocumentService>();
        services.AddTransient<ITenantService, TenantService>();
        services.AddTransient<IChatService, ChatService>();

        return services.BuildServiceProvider();
    }
}
