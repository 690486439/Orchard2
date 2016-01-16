using Microsoft.Extensions.DependencyInjection;
using Orchard.Environment;
using Orchard.Environment.Commands;
using Orchard.Hosting.FileSystem;

namespace Orchard.Hosting
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddWebHost(this IServiceCollection services)
        {
            return services.AddHost(internalServices =>
            {
                internalServices.AddLogging();
                internalServices.AddOptions();
                internalServices.AddLocalization();
                internalServices.AddHostCore();
                internalServices.AddExtensionManager();
                internalServices.AddCommands();

                internalServices.AddWebFileSystem();
            });
        }
    }
}