using Veso.Api;
using Veso.Api.Auth;
using Veso.Api.Auth.FirstTimeSetupOrElevatedPolicy;
using Veso.Api.Auth.RequiresElevationPolicy;
using Veso.Api.Constants;
using Veso.Api.Controllers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;

namespace Veso.Server.Extensions
{
    /// <summary>
    /// API specific extensions for the service collection.
    /// </summary>
    public static class ApiServiceCollectionExtensions
    {
        /// <summary>
        /// Adds veso API authorization policies to the DI container.
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddvesoApiAuthorization(this IServiceCollection serviceCollection)
        {
            serviceCollection.AddSingleton<IAuthorizationHandler, FirstTimeSetupOrElevatedHandler>();
            serviceCollection.AddSingleton<IAuthorizationHandler, RequiresElevationHandler>();
            return serviceCollection.AddAuthorizationCore(options =>
            {
                options.AddPolicy(
                    Policies.RequiresElevation,
                    policy =>
                    {
                        policy.AddAuthenticationSchemes(AuthenticationSchemes.CustomAuthentication);
                        policy.AddRequirements(new RequiresElevationRequirement());
                    });
                options.AddPolicy(
                    Policies.FirstTimeSetupOrElevated,
                    policy =>
                    {
                        policy.AddAuthenticationSchemes(AuthenticationSchemes.CustomAuthentication);
                        policy.AddRequirements(new FirstTimeSetupOrElevatedRequirement());
                    });
            });
        }

        /// <summary>
        /// Adds custom legacy authentication to the service collection.
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <returns>The updated service collection.</returns>
        public static AuthenticationBuilder AddCustomAuthentication(this IServiceCollection serviceCollection)
        {
            return serviceCollection.AddAuthentication(AuthenticationSchemes.CustomAuthentication)
                .AddScheme<AuthenticationSchemeOptions, CustomAuthenticationHandler>(AuthenticationSchemes.CustomAuthentication, null);
        }

        /// <summary>
        /// Extension method for adding the veso API to the service collection.
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <param name="baseUrl">The base url for the API.</param>
        /// <returns>The MVC builder.</returns>
        public static IMvcBuilder AddvesoApi(this IServiceCollection serviceCollection, string baseUrl)
        {
            return serviceCollection.AddMvc(opts =>
                {
                    opts.UseGeneralRoutePrefix(baseUrl);
                })

                // Clear app parts to avoid other assemblies being picked up
                .ConfigureApplicationPartManager(a => a.ApplicationParts.Clear())
                .AddApplicationPart(typeof(StartupController).Assembly)
                .AddControllersAsServices();
        }

        /// <summary>
        /// Adds Swagger to the service collection.
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddvesoApiSwagger(this IServiceCollection serviceCollection)
        {
            return serviceCollection.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "veso API", Version = "v1" });
            });
        }
    }
}
