﻿namespace NServiceBus.Storage.MongoDB;

using System;
using System.Collections.Generic;
using System.Linq;
using Features;
using global::MongoDB.Bson.Serialization;
using global::MongoDB.Bson.Serialization.Options;
using global::MongoDB.Bson.Serialization.Serializers;
using global::MongoDB.Driver;
using Microsoft.Extensions.DependencyInjection;
using Outbox;

class OutboxStorage : Feature
{
    OutboxStorage()
    {
        Defaults(s => s.EnableFeatureByDefault<SynchronizedStorage>());

        DependsOn<Outbox>();
        DependsOn<SynchronizedStorage>();
    }

    protected override void Setup(FeatureConfigurationContext context)
    {
        if (context.Settings.TryGet(SettingsKeys.UseTransactions, out bool useTransactions) && useTransactions == false)
        {
            throw new Exception(
                $"Transactions are required when the Outbox is enabled, but they have been disabled by calling 'EndpointConfiguration.UsePersistence<{nameof(MongoPersistence)}>().UseTransactions(false)'.");
        }

        var client = context.Settings.Get<Func<IMongoClient>>(SettingsKeys.MongoClient)();
        var databaseName = context.Settings.Get<string>(SettingsKeys.DatabaseName);
        var collectionNamingConvention =
            context.Settings.Get<Func<Type, string>>(SettingsKeys.CollectionNamingConvention);

        if (!context.Settings.TryGet(SettingsKeys.TimeToKeepOutboxDeduplicationData,
                out TimeSpan timeToKeepOutboxDeduplicationData))
        {
            timeToKeepOutboxDeduplicationData = TimeSpan.FromDays(7);
        }

        InitializeOutboxTypes(client, databaseName, collectionNamingConvention, timeToKeepOutboxDeduplicationData);

        context.Services.AddSingleton<IOutboxStorage>(new OutboxPersister(client, databaseName,
            collectionNamingConvention));
    }

    internal static void InitializeOutboxTypes(IMongoClient client, string databaseName,
        Func<Type, string> collectionNamingConvention, TimeSpan timeToKeepOutboxDeduplicationData)
    {
        if (!BsonClassMap.IsClassMapRegistered(typeof(StorageTransportOperation)))
        {
            BsonClassMap.RegisterClassMap<StorageTransportOperation>(cm =>
            {
                cm.AutoMap();
                cm.MapMember(c => c.Headers)
                    .SetSerializer(
                        new DictionaryInterfaceImplementerSerializer<Dictionary<string, string>>(
                            DictionaryRepresentation.ArrayOfDocuments));
                cm.MapMember(c => c.Options)
                    .SetSerializer(
                        new DictionaryInterfaceImplementerSerializer<Dictionary<string, string>>(
                            DictionaryRepresentation.ArrayOfDocuments));
            });
        }

        var collectionSettings = new MongoCollectionSettings
        {
            ReadConcern = ReadConcern.Majority,
            ReadPreference = ReadPreference.Primary,
            WriteConcern = WriteConcern.WMajority
        };

        var outboxCollection = client.GetDatabase(databaseName)
            .GetCollection<OutboxRecord>(collectionNamingConvention(typeof(OutboxRecord)), collectionSettings);
        var outboxCleanupIndex = outboxCollection.Indexes.List().ToList().SingleOrDefault(indexDocument =>
            indexDocument.GetElement("name").Value == OutboxCleanupIndexName);
        var createIndex = false;
        if (outboxCleanupIndex is null)
        {
            createIndex = true;
        }
        else if (!outboxCleanupIndex.TryGetElement("expireAfterSeconds", out var existingExpiration) ||
                 TimeSpan.FromSeconds(existingExpiration.Value.ToInt32()) != timeToKeepOutboxDeduplicationData)
        {
            outboxCollection.Indexes.DropOne(OutboxCleanupIndexName);
            createIndex = true;
        }

        if (!createIndex)
        {
            return;
        }

        var indexModel = new CreateIndexModel<OutboxRecord>(
            Builders<OutboxRecord>.IndexKeys.Ascending(record => record.Dispatched),
            new CreateIndexOptions
            {
                ExpireAfter = timeToKeepOutboxDeduplicationData,
                Name = OutboxCleanupIndexName,
                Background = true
            });
        outboxCollection.Indexes.CreateOne(indexModel);
    }

    internal const string OutboxCleanupIndexName = "OutboxCleanup";
}