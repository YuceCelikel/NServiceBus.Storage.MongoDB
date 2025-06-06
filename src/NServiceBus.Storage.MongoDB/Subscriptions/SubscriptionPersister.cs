﻿namespace NServiceBus.Storage.MongoDB;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Extensibility;
using global::MongoDB.Driver;
using Logging;
using Unicast.Subscriptions;
using Unicast.Subscriptions.MessageDrivenSubscriptions;

class SubscriptionPersister : ISubscriptionStorage
{
    public SubscriptionPersister(IMongoCollection<EventSubscription> subscriptionsCollection)
    {
        this.subscriptionsCollection = subscriptionsCollection;
    }

    public Task Subscribe(Subscriber subscriber, MessageType messageType, ContextBag context,
        CancellationToken cancellationToken = default)
    {
        var subscription = new EventSubscription
        {
            MessageTypeName = messageType.TypeName,
            TransportAddress = subscriber.TransportAddress,
            Endpoint = subscriber.Endpoint
        };

        if (IsLegacySubscription(subscription))
        {
            // support for older versions of NServiceBus which do not provide a logical endpoint name. We do not want to replace a non-null value with null.
            return AddLegacySubscription(subscription, cancellationToken);
        }

        return AddOrUpdateSubscription(subscription, cancellationToken);
    }

    public async Task Unsubscribe(Subscriber subscriber, MessageType messageType, ContextBag context,
        CancellationToken cancellationToken = default)
    {
        var filter = filterBuilder.And(
            filterBuilder.Eq(s => s.MessageTypeName, messageType.TypeName),
            filterBuilder.Eq(s => s.TransportAddress, subscriber.TransportAddress));
        var result = await subscriptionsCollection.DeleteManyAsync(filter, cancellationToken).ConfigureAwait(false);

        Log.DebugFormat("Deleted {0} subscriptions for address '{1}' on message type '{2}'", result.DeletedCount,
            subscriber.TransportAddress, messageType.TypeName);
    }

    public async Task<IEnumerable<Subscriber>> GetSubscriberAddressesForMessage(IEnumerable<MessageType> messageTypes,
        ContextBag context, CancellationToken cancellationToken = default)
    {
        var messageTypeNames = new List<string>();
        foreach (var messageType in messageTypes)
        {
            messageTypeNames.Add(messageType.TypeName);
        }

        var filter = filterBuilder.In(s => s.MessageTypeName, messageTypeNames);
        // This projection allows a covered query:
        var projection = Builders<EventSubscription>.Projection
            .Include(s => s.TransportAddress)
            .Include(s => s.Endpoint)
            .Exclude("_id");

        // == Following is used to view index usage for the query ==
        //var options = new FindOptions();
        //options.Modifiers = new global::MongoDB.Bson.BsonDocument("$explain", true);
        //var queryStats = await subscriptionsCollection
        //    .WithReadConcern(ReadConcern.Default)
        //    .Find(filter, options)
        //    .Project(projection)
        //    .ToListAsync()
        //    .ConfigureAwait(false);
        // =========================================================
        var subscriptions = await subscriptionsCollection
            .Find(filter)
            .Project(projection)
            .ToListAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var result = new List<Subscriber>(subscriptions.Count);
        foreach (var subscription in subscriptions)
        {
            result.Add(new Subscriber(
                subscription[nameof(EventSubscription.TransportAddress)].AsString,
                subscription[nameof(EventSubscription.Endpoint)].IsBsonNull
                    ? null
                    : subscription[nameof(EventSubscription.Endpoint)].AsString));
        }

        return result;
    }

    static bool IsLegacySubscription(EventSubscription subscription) => subscription.Endpoint == null;

    async Task AddLegacySubscription(EventSubscription subscription, CancellationToken cancellationToken)
    {
        try
        {
            await subscriptionsCollection.InsertOneAsync(subscription, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            Log.DebugFormat("Created legacy subscription for '{0}' on '{1}'", subscription.TransportAddress,
                subscription.MessageTypeName);
        }
        catch (MongoWriteException e) when (e.WriteError?.Code == DuplicateKeyErrorCode)
        {
            // duplicate key error which means a document already exists
            // existing subscriptions should not be stripped of their logical endpoint name
            Log.DebugFormat(
                "Skipping legacy subscription for '{0}' on '{1}' because a newer subscription already exists",
                subscription.TransportAddress, subscription.MessageTypeName);
        }
    }

    async Task AddOrUpdateSubscription(EventSubscription subscription, CancellationToken cancellationToken)
    {
        try
        {
            var filter = filterBuilder.And(
                filterBuilder.Eq(s => s.MessageTypeName, subscription.MessageTypeName),
                filterBuilder.Eq(s => s.TransportAddress, subscription.TransportAddress));
            var update = Builders<EventSubscription>.Update.Set(s => s.Endpoint, subscription.Endpoint);
            var options = new UpdateOptions { IsUpsert = true };

            var result = await subscriptionsCollection.UpdateOneAsync(filter, update, options, cancellationToken)
                .ConfigureAwait(false);
            if (result.ModifiedCount > 0)
            {
                // ModifiedCount is also 0 when the update values match exactly the existing document.
                Log.DebugFormat("Updated existing subscription of '{0}' on '{1}'", subscription.TransportAddress,
                    subscription.MessageTypeName);
            }
            else if (result.UpsertedId != null)
            {
                Log.DebugFormat("Created new subscription for '{0}' on '{1}'", subscription.TransportAddress,
                    subscription.MessageTypeName);
            }
        }
        catch (MongoWriteException e) when (e.WriteError?.Code == DuplicateKeyErrorCode)
        {
            // This is thrown when there is a race condition and the same subscription has been added already.
            // As upserts create new documents, those operations aren't atomic in regards to concurrent upserts
            // and duplicate documents will only be prevented by the unique key constraint.
        }
    }

    public void CreateIndexes()
    {
        var uniqueIndex = new CreateIndexModel<EventSubscription>(Builders<EventSubscription>.IndexKeys
                .Ascending(x => x.MessageTypeName)
                .Ascending(x => x.TransportAddress),
            new CreateIndexOptions { Unique = true });
        var searchIndex = new CreateIndexModel<EventSubscription>(Builders<EventSubscription>.IndexKeys
            .Ascending(x => x.MessageTypeName)
            .Ascending(x => x.TransportAddress)
            .Ascending(x => x.Endpoint));
        subscriptionsCollection.Indexes.CreateMany(new[] { uniqueIndex, searchIndex });
    }

    IMongoCollection<EventSubscription> subscriptionsCollection;
    const int DuplicateKeyErrorCode = 11000;
    static readonly ILog Log = LogManager.GetLogger<SubscriptionPersister>();
    static readonly FilterDefinitionBuilder<EventSubscription> filterBuilder = Builders<EventSubscription>.Filter;
}