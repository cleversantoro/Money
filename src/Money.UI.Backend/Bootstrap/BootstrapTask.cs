﻿using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Money.Data;
using Money.Hubs;
using Money.Services;
using Neptuo;
using Neptuo.Activators;
using Neptuo.Bootstrap;
using Neptuo.Commands;
using Neptuo.Converters;
using Neptuo.Data;
using Neptuo.Events;
using Neptuo.Exceptions.Handlers;
using Neptuo.Formatters;
using Neptuo.Formatters.Converters;
using Neptuo.Formatters.Metadata;
using Neptuo.Logging;
using Neptuo.Logging.Serialization;
using Neptuo.Models.Repositories;
using Neptuo.Models.Snapshots;
using Neptuo.Queries;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Money.Bootstrap
{
    public class BootstrapTask : IBootstrapTask
    {
        private readonly IServiceCollection services;
        private readonly ConnectionStrings connectionStrings;

        private ILogFactory logFactory;
        private ILog errorLog;
        private IFactory<ReadModelContext> readModelContextFactory;
        private IFactory<EventSourcingContext> eventSourcingContextFactory;

        private PriceCalculator priceCalculator;
        private ICompositeTypeProvider typeProvider;

        private ExceptionHandlerBuilder exceptionHandlerBuilder;

        private DefaultQueryDispatcher queryDispatcher;
        private PersistentCommandDispatcher commandDispatcher;
        private PersistentEventDispatcher eventDispatcher;

        private EntityEventStore eventStore;

        private IFormatter commandFormatter;
        private IFormatter eventFormatter;
        private IFormatter queryFormatter;
        private IFormatter exceptionFormatter;

        public BootstrapTask(IServiceCollection services, ConnectionStrings connectionStrings)
        {
            Ensure.NotNull(services, "services");
            Ensure.NotNull(connectionStrings, "connectionStrings");
            this.services = services;
            this.connectionStrings = connectionStrings;
        }

        public void Initialize()
        {
            logFactory = new DefaultLogFactory("Root").AddSerializer(new ConsoleSerializer());
            errorLog = logFactory.Scope("Error");

            readModelContextFactory = Factory.Getter(() => new ReadModelContext(connectionStrings.ReadModel));
            eventSourcingContextFactory = Factory.Getter(() => new EventSourcingContext(connectionStrings.EventSourcing));
            CreateReadModelContext();
            CreateEventSourcingContext();

            exceptionHandlerBuilder = new ExceptionHandlerBuilder();

            services
                .AddSingleton(readModelContextFactory)
                .AddSingleton(eventSourcingContextFactory)
                .AddSingleton(exceptionHandlerBuilder)
                .AddSingleton<IExceptionHandler>(exceptionHandlerBuilder);

            Domain();

            priceCalculator = new PriceCalculator(eventDispatcher.Handlers, queryDispatcher);

            services
                .AddSingleton(priceCalculator)
                .AddSingleton(new FormatterContainer(commandFormatter, eventFormatter, queryFormatter, exceptionFormatter));

            ReadModels();

            services
                .AddSingleton<IEventHandlerCollection>(eventDispatcher.Handlers)
                .AddScoped<ICommandDispatcher>(provider => new UserCommandDispatcher(commandDispatcher, provider.GetService<IHttpContextAccessor>().HttpContext, provider.GetService<ApiHub>()))
                .AddScoped<IQueryDispatcher>(provider => new UserQueryDispatcher(queryDispatcher, provider.GetService<IHttpContextAccessor>().HttpContext));

            CurrencyCache currencyCache = new CurrencyCache(eventDispatcher.Handlers, queryDispatcher, queryDispatcher);

            services
                .AddSingleton(currencyCache);
        }

        private void Domain()
        {
            Converts.Repository
                .AddStringTo<int>(Int32.TryParse)
                .AddStringTo<bool>(Boolean.TryParse)
                .AddEnumSearchHandler(false)
                .AddJsonEnumSearchHandler()
                .AddJsonPrimitivesSearchHandler()
                .AddJsonObjectSearchHandler()
                .AddJsonKey()
                .AddJsonTimeSpan()
                .Add(new ColorConverter())
                .AddToStringSearchHandler();

            eventStore = new EntityEventStore(eventSourcingContextFactory);
            eventDispatcher = new PersistentEventDispatcher(new EmptyEventStore());
            eventDispatcher.DispatcherExceptionHandlers.Add(exceptionHandlerBuilder);
            eventDispatcher.EventExceptionHandlers.Add(exceptionHandlerBuilder);

            IFactory<ICompositeStorage> compositeStorageFactory = Factory.Default<JsonCompositeStorage>();

            typeProvider = new ReflectionCompositeTypeProvider(
                new ReflectionCompositeDelegateFactory(),
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
            );

            commandFormatter = new CompositeCommandFormatter(typeProvider, compositeStorageFactory);
            eventFormatter = new CompositeEventFormatter(typeProvider, compositeStorageFactory, new List<ICompositeFormatterExtender>() { new UserKeyEventExtender() });
            queryFormatter = new CompositeListFormatter(typeProvider, compositeStorageFactory, logFactory);
            exceptionFormatter = new CompositeExceptionFormatter(typeProvider, compositeStorageFactory);

            commandDispatcher = new PersistentCommandDispatcher(new SerialCommandDistributor(), new EmptyCommandStore(), commandFormatter);
            commandDispatcher.DispatcherExceptionHandlers.Add(exceptionHandlerBuilder);
            commandDispatcher.CommandExceptionHandlers.Add(exceptionHandlerBuilder);

            queryDispatcher = new DefaultQueryDispatcher();

            var outcomeRepository = new AggregateRootRepository<Outcome>(
                eventStore,
                eventFormatter,
                new ReflectionAggregateRootFactory<Outcome>(),
                eventDispatcher,
                new NoSnapshotProvider(),
                new EmptySnapshotStore()
            );

            var categoryRepository = new AggregateRootRepository<Category>(
                eventStore,
                eventFormatter,
                new ReflectionAggregateRootFactory<Category>(),
                eventDispatcher,
                new NoSnapshotProvider(),
                new EmptySnapshotStore()
            );

            var currencyListRepository = new AggregateRootRepository<CurrencyList>(
                eventStore,
                eventFormatter,
                new ReflectionAggregateRootFactory<CurrencyList>(),
                eventDispatcher,
                new NoSnapshotProvider(),
                new EmptySnapshotStore()
            );

            Money.BootstrapTask bootstrapTask = new Money.BootstrapTask(
                commandDispatcher.Handlers,
                Factory.Instance(outcomeRepository),
                Factory.Instance(categoryRepository),
                Factory.Instance(currencyListRepository)
            );

            bootstrapTask.Initialize();
        }

        private void ReadModels()
        {
            Models.Builders.BootstrapTask bootstrapTask = new Models.Builders.BootstrapTask(
                queryDispatcher,
                eventDispatcher.Handlers,
                readModelContextFactory,
                priceCalculator
            );

            bootstrapTask.Initialize();
        }

        private void CreateEventSourcingContext()
        {
            using (var eventSourcing = eventSourcingContextFactory.Create())
                eventSourcing.Database.EnsureCreated();
        }

        private void CreateReadModelContext()
        {
            using (var readModels = readModelContextFactory.Create())
                readModels.Database.EnsureCreated();
        }

        public void Handle(Exception exception)
            => errorLog.Error(exception);
    }
}
