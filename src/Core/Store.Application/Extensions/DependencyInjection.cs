﻿using System.Reflection;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Store.Application.CQRS.Logging;
using Store.Application.CQRS.Logging.Interfaces;
using Store.Application.CQRS.Validation;
using Store.Application.CQRS.Validation.Interfaces;
using Store.Application.Mapper;

namespace Store.Application.Extensions;

public static class DependencyInjection
{

    public static IServiceCollection AddMapper(this IServiceCollection services)
    {
        services.AddAutoMapper(config => { config.AddProfile(new AssemblyMappingProfile(typeof(IMapWith<>).Assembly)); });

        return services;
    }

    public static IServiceCollection AddMediatr(this IServiceCollection services)
    {
        return services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblies(Assembly.GetExecutingAssembly()));
    }

    public static IServiceCollection AddValidatorBehavior(this IServiceCollection services)
    {
        services.Scan(scan => scan
            .FromAssemblyOf<IValidationHandler>()
            .AddClasses(classes => classes.AssignableTo<IValidationHandler>())
            .AsImplementedInterfaces()
            .WithTransientLifetime());

        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehaviour<,>));

        return services;
    }

    public static IServiceCollection AddLoggerBehavior(this IServiceCollection services)
    {
        services.Scan(scan => scan
            .FromAssemblyOf<ILoggingBehavior>()
            .AddClasses(classes => classes.AssignableTo<ILoggingBehavior>())
            .AsImplementedInterfaces()
            .WithTransientLifetime());

        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));

        return services;
    }

}