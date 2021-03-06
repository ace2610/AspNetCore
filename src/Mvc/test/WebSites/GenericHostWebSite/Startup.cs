// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace GenericHostWebSite
{
    public class Startup
    {
        // Set up application services
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton(new TestGenericService { Message = "true" });

            services
                .AddMvc(options =>
                {
                    // Remove when all URL generation tests are passing - https://github.com/aspnet/Routing/issues/590
                    options.EnableEndpointRouting = false;
                })
                .SetCompatibilityVersion(CompatibilityVersion.Latest)
                .AddNewtonsoftJson()
                .AddXmlDataContractSerializerFormatters();

            services.AddLogging();
            services.AddHttpContextAccessor();
            services.AddScoped<TestResponseGenerator>();
            services.AddSingleton<IActionContextAccessor, ActionContextAccessor>();
        }

        public void Configure(IApplicationBuilder app)
        {
            app.UseDeveloperExceptionPage();

            app.UseStaticFiles();

            // Add MVC to the request pipeline
            app.UseMvc(routes =>
            {
                routes.MapRoute(
                    "areaRoute",
                    "{area:exists}/{controller}/{action}",
                    new { controller = "Home", action = "Index" });

                routes.MapRoute("ActionAsMethod", "{controller}/{action}",
                    defaults: new { controller = "Home", action = "Index" });

                routes.MapRoute("PageRoute", "{controller}/{action}/{page}");
            });
        }
    }
}
