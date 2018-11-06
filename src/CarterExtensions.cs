namespace Carter
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Threading.Tasks;
    using FluentValidation;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Routing;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;

    public static class CarterExtensions
    {
        /// <summary>
        /// Adds Carter to the specified <see cref="IApplicationBuilder"/>.
        /// </summary>
        /// <param name="builder">The <see cref="IApplicationBuilder"/> to configure.</param>
        /// <param name="options">A <see cref="CarterOptions"/> instance.</param>
        /// <returns>A reference to this instance after the operation has completed.</returns>
        public static IApplicationBuilder UseCarter(this IApplicationBuilder builder, CarterOptions options = null)
        {
            var diagnostics = builder.ApplicationServices.GetService<CarterDiagnostics>();

            var logger = builder.ApplicationServices.GetService<ILoggerFactory>().CreateLogger(typeof(CarterDiagnostics));
            diagnostics.LogDiscoveredCarterTypes(logger);

            ApplyGlobalBeforeHook(builder, options);

            ApplyGlobalAfterHook(builder, options);

            var routeBuilder = new RouteBuilder(builder);

            //Create a "startup scope" to resolve modules from
            using (var scope = builder.ApplicationServices.CreateScope())
            {
                //Get all instances of CarterModule to fetch and register declared routes
                foreach (var module in scope.ServiceProvider.GetServices<CarterModule>())
                {
                    var moduleType = module.GetType();
					var distinctRoutes = module.Routes.Keys.Select(route => new Tuple<string, string>(route.verb, route.path)).Distinct();

					foreach (var path in distinctRoutes)
                    {
						routeBuilder.MapVerb(path.Item1, path.Item2, CreateRouteHandler(path.Item2, moduleType));
                    }
                }
            }

            return builder.UseRouter(routeBuilder.Build());
        }

	    private static Dictionary<Type,CarterModule> _moduleDictionary = new Dictionary<Type, CarterModule>();
	    private static Dictionary<int,IStatusCodeHandler> _statusCodeHandlers = new Dictionary<int, IStatusCodeHandler>();

        private static RequestDelegate CreateRouteHandler(string path, Type moduleType)
        {
            return async ctx =>
            {
	            CarterModule module = null;

	            if (_moduleDictionary.ContainsKey(moduleType))
	            {
		            module = _moduleDictionary[moduleType];
	            }
	            else
	            {
		            module = ctx.RequestServices.GetRequiredService(moduleType) as CarterModule;
		            // lock and check again before adding
		            lock (_moduleDictionary)
		            {
			            if (!_moduleDictionary.ContainsKey(moduleType))
				            _moduleDictionary.Add(moduleType, module);
		            }
	            }

	            if (module == null)
	            {
		            // if we can't find a module, then return a 404
		            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
		            return;
	            }
                var loggerFactory = ctx.RequestServices.GetService<ILoggerFactory>();
                var logger = loggerFactory.CreateLogger(moduleType);

                if (!module.Routes.TryGetValue((ctx.Request.Method, path), out var routeHandler))
                {
                    // if the path was registered but a handler matching the
                    // current method was not found, return MethodNotFound
                    ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    return;
                }

                // begin handling the request
                if (HttpMethods.IsHead(ctx.Request.Method))
                {
                    //Cannot read the default stream once WriteAsync has been called on it
                    ctx.Response.Body = new MemoryStream();
                }

                // run the module handlers
                bool shouldContinue = true;

                if (module.Before != null)
                {
                    foreach (var beforeDelegate in module.Before.GetInvocationList())
                    {
                        var beforeTask = (Task<bool>)beforeDelegate.DynamicInvoke(ctx);
                        shouldContinue = await beforeTask;
                        if (!shouldContinue)
                        {
                            break;
                        }
                    }
                }

                if (shouldContinue)
                {
                    // run the route handler
                    logger.LogDebug("Executing module route handler for {Method} /{Path}", ctx.Request.Method, path);
                    await routeHandler(ctx);

                    // run after handler
                    if (module.After != null)
                    {
                        await module.After(ctx);
                    }
                }

	            // run status code handler
	            IStatusCodeHandler scHandler = null;
	            if (_statusCodeHandlers.ContainsKey(ctx.Response.StatusCode))
	            {
		            scHandler = _statusCodeHandlers[ctx.Response.StatusCode];
	            }
	            else
	            {
		            var statusCodeHandlers = ctx.RequestServices.GetServices<IStatusCodeHandler>();
		            scHandler = statusCodeHandlers.FirstOrDefault(x => x.CanHandle(ctx.Response.StatusCode));
		            // lock and check again before adding
		            lock (_statusCodeHandlers)
		            {
			            if (!_statusCodeHandlers.ContainsKey(ctx.Response.StatusCode))
				            _statusCodeHandlers.Add(ctx.Response.StatusCode, scHandler);
		            }
	            }

                if (scHandler != null)
                {
                    await scHandler.Handle(ctx);
                }

                if (HttpMethods.IsHead(ctx.Request.Method))
                {
                    var length = ctx.Response.Body.Length;
                    ctx.Response.Body.SetLength(0);
                    ctx.Response.ContentLength = length;
                }
            };
        }

        private static void ApplyGlobalAfterHook(IApplicationBuilder builder, CarterOptions options)
        {
            if (options?.After != null)
            {
                builder.Use(async (ctx, next) =>
                {
                    var loggerFactory = ctx.RequestServices.GetService<ILoggerFactory>();
                    var logger = loggerFactory.CreateLogger("Carter.GlobalAfterHook");
                    await next();
                    logger.LogDebug("Executing global after hook");
                    await options.After(ctx);
                });
            }
        }

        private static void ApplyGlobalBeforeHook(IApplicationBuilder builder, CarterOptions options)
        {
            if (options?.Before != null)
            {
                builder.Use(async (ctx, next) =>
                {
                    var loggerFactory = ctx.RequestServices.GetService<ILoggerFactory>();
                    var logger = loggerFactory.CreateLogger("Carter.GlobalBeforeHook");
                    logger.LogDebug("Executing global before hook");

                    var carryOn = await options.Before(ctx);
                    if (carryOn)
                    {
                        logger.LogDebug("Executing next handler after global before hook");
                        await next();
                    }
                });
            }
        }

        /// <summary>
        /// Adds Carter to the specified <see cref="IServiceCollection"/>.
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> to add Carter to.</param>
        public static void AddCarter(this IServiceCollection services)
        {
            var assemblyCatalog = new DependencyContextAssemblyCatalog();

            var assemblies = assemblyCatalog.GetAssemblies();

            CarterDiagnostics diagnostics = new CarterDiagnostics();
            services.AddSingleton(diagnostics);

            var validators = assemblies.SelectMany(ass => ass.GetTypes())
                .Where(typeof(IValidator).IsAssignableFrom)
                .Where(t => !t.GetTypeInfo().IsAbstract);

            foreach (var validator in validators)
            {
                diagnostics.AddValidator(validator);
                services.AddSingleton(typeof(IValidator), validator);
            }

            services.AddSingleton<IValidatorLocator, DefaultValidatorLocator>();

            services.AddRouting();

            var modules = assemblies.SelectMany(x => x.GetTypes()
                .Where(t =>
                    !t.IsAbstract &&
                    typeof(CarterModule).IsAssignableFrom(t) &&
                    t != typeof(CarterModule) &&
                    t.IsPublic
                ));

            foreach (var module in modules)
            {
                diagnostics.AddModule(module);
                services.AddSingleton(module);
                services.AddSingleton(typeof(CarterModule), module);
            }

            var schs = assemblies.SelectMany(x => x.GetTypes().Where(t => typeof(IStatusCodeHandler).IsAssignableFrom(t) && t != typeof(IStatusCodeHandler)));
            foreach (var sch in schs)
            {
                diagnostics.AddStatusCodeHandler(sch);
                services.AddScoped(typeof(IStatusCodeHandler), sch);
            }

            var responseNegotiators = assemblies.SelectMany(x => x.GetTypes()
                .Where(t =>
                    !t.IsAbstract &&
                    typeof(IResponseNegotiator).IsAssignableFrom(t) &&
                    t != typeof(IResponseNegotiator) &&
                    t != typeof(DefaultJsonResponseNegotiator)
                ));

            foreach (var negotiatator in responseNegotiators)
            {
                diagnostics.AddResponseNegotiator(negotiatator);
                services.AddSingleton(typeof(IResponseNegotiator), negotiatator);
            }

            services.AddSingleton<IResponseNegotiator, DefaultJsonResponseNegotiator>();
        }
    }
}
