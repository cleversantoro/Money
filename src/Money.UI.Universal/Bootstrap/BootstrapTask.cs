﻿using Money.Services;
using Money.Services.Globalization;
using Money.Services.Models.Builders;
using Money.Services.Settings;
using Money.Services.Tiles;
using Money.ViewModels;
using Neptuo;
using Neptuo.Activators;
using Neptuo.Converters;
using Neptuo.Data;
using Neptuo.Events;
using Neptuo.Exceptions.Handlers;
using Neptuo.Formatters;
using Neptuo.Formatters.Converters;
using Neptuo.Formatters.Metadata;
using Neptuo.Models.Keys;
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
    public class BootstrapTask : IExceptionHandler
    {
        private PersistentEventDispatcher eventDispatcher;
        private ICompositeTypeProvider typeProvider;
        private DefaultQueryDispatcher queryDispatcher;

        public IRepository<Outcome, IKey> OutcomeRepository { get; private set; }
        public IRepository<Category, IKey> CategoryRepository { get; private set; }
        public IRepository<CurrencyList, IKey> CurrencyListRepository { get; private set; }
        public IDomainFacade DomainFacade { get; private set; }

        public EntityEventStore EventStore { get; private set; }

        public IFormatter EventFormatter { get; private set; }

        public void Initialize()
        {
            ServiceProvider.MessageBuilder = new MessageBuilder();
            ServiceProvider.MainMenuFactory = new MainMenuListFactory();
            ServiceProvider.CurrencyProvider = new DefaultCurrencyProvider();

            StorageFactory storageFactory = new StorageFactory();
            ServiceProvider.EventSourcingContextFactory = storageFactory;
            ServiceProvider.ReadModelContextFactory = storageFactory;
            ServiceProvider.StorageContainerFactory = storageFactory;

            Domain();

            PriceCalculator priceCalculator = new PriceCalculator(eventDispatcher.Handlers);
            ServiceProvider.PriceConverter = priceCalculator;

            ReadModels();

            ServiceProvider.QueryDispatcher = queryDispatcher;
            ServiceProvider.DomainFacade = DomainFacade;
            ServiceProvider.EventHandlers = eventDispatcher.Handlers;

            UpgradeService upgradeService = new UpgradeService(DomainFacade, EventStore, EventFormatter, storageFactory, storageFactory, storageFactory, priceCalculator);
            ServiceProvider.UpgradeService = upgradeService;

            ServiceProvider.TileService = new TileService();
            ServiceProvider.DevelopmentTools = new DevelopmentService(upgradeService, storageFactory);
            ServiceProvider.UserPreferences = new ApplicationSettingsService(new CompositeTypeFormatterFactory(typeProvider), storageFactory);

            CurrencyCache currencyCache = new CurrencyCache(eventDispatcher.Handlers, queryDispatcher);

            currencyCache.InitializeAsync(queryDispatcher);
            priceCalculator.InitializeAsync(queryDispatcher);
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
            
            EventStore = new EntityEventStore(ServiceProvider.EventSourcingContextFactory);
            eventDispatcher = new PersistentEventDispatcher(new EmptyEventStore());
            eventDispatcher.DispatcherExceptionHandlers.Add(this);
            eventDispatcher.EventExceptionHandlers.Add(this);

            IFactory<ICompositeStorage> compositeStorageFactory = Factory.Default<JsonCompositeStorage>();

            typeProvider = new ReflectionCompositeTypeProvider(
                new ReflectionCompositeDelegateFactory(),
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
            );

            EventFormatter = new CompositeEventFormatter(typeProvider, compositeStorageFactory);

            OutcomeRepository = new AggregateRootRepository<Outcome>(
                EventStore,
                EventFormatter,
                new ReflectionAggregateRootFactory<Outcome>(),
                eventDispatcher,
                new NoSnapshotProvider(),
                new EmptySnapshotStore()
            );

            CategoryRepository = new AggregateRootRepository<Category>(
                EventStore,
                EventFormatter,
                new ReflectionAggregateRootFactory<Category>(),
                eventDispatcher,
                new NoSnapshotProvider(),
                new EmptySnapshotStore()
            );

            CurrencyListRepository = new AggregateRootRepository<CurrencyList>(
                EventStore,
                EventFormatter,
                new ReflectionAggregateRootFactory<CurrencyList>(),
                eventDispatcher,
                new NoSnapshotProvider(),
                new EmptySnapshotStore()
            );

            DomainFacade = new DefaultDomainFacade(
                OutcomeRepository,
                CategoryRepository,
                CurrencyListRepository
            );
        }

        private void ReadModels()
        {
            // Should match with RecreateReadModelContext.
            queryDispatcher = new DefaultQueryDispatcher();

            CategoryBuilder categoryBuilder = new CategoryBuilder(ServiceProvider.ReadModelContextFactory);
            queryDispatcher.AddAll(categoryBuilder);
            eventDispatcher.Handlers.AddAll(categoryBuilder);

            OutcomeBuilder outcomeBuilder = new OutcomeBuilder(ServiceProvider.ReadModelContextFactory, ServiceProvider.PriceConverter);
            queryDispatcher.AddAll(outcomeBuilder);
            eventDispatcher.Handlers.AddAll(outcomeBuilder);

            CurrencyBuilder currencyBuilder = new CurrencyBuilder(ServiceProvider.ReadModelContextFactory);
            queryDispatcher.AddAll(currencyBuilder);
            eventDispatcher.Handlers.AddAll(currencyBuilder);
        }
        
        public void Handle(Exception exception)
        {
            throw new NotImplementedException();
        }
    }
}