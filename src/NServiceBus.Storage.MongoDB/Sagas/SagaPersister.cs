﻿namespace NServiceBus.Storage.MongoDB
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Extensibility;
    using global::MongoDB.Bson;
    using global::MongoDB.Bson.Serialization;
    using global::MongoDB.Driver;
    using Persistence;
    using Sagas;

    class SagaPersister : ISagaPersister
    {
        public SagaPersister(string versionElementName)
        {
            this.versionElementName = versionElementName;
        }

        public async Task Save(IContainSagaData sagaData, SagaCorrelationProperty correlationProperty, ISynchronizedStorageSession session, ContextBag context, CancellationToken cancellationToken = default)
        {
            var storageSession = ((SynchronizedStorageSession)session).Session;
            var sagaDataType = sagaData.GetType();

            var document = sagaData.ToBsonDocument(sagaDataType);
            document.Add(versionElementName, 0);

            await storageSession.InsertOneAsync(sagaDataType, document, cancellationToken).ConfigureAwait(false);
        }

        public async Task Update(IContainSagaData sagaData, ISynchronizedStorageSession session, ContextBag context, CancellationToken cancellationToken = default)
        {
            var storageSession = ((SynchronizedStorageSession)session).Session;
            var sagaDataType = sagaData.GetType();

            var version = storageSession.RetrieveVersion(sagaDataType);
            var document = sagaData.ToBsonDocument(sagaDataType).SetElement(new BsonElement(versionElementName, version + 1));

            var result = await storageSession.ReplaceOneAsync(sagaDataType, filterBuilder.Eq(idElementName, sagaData.Id) & filterBuilder.Eq(versionElementName, version), document, cancellationToken).ConfigureAwait(false);

            if (result.ModifiedCount != 1)
            {
                throw new Exception($"The '{sagaDataType.Name}' saga with id '{sagaData.Id}' was updated by another process or no longer exists.");
            }
        }

        public Task<TSagaData> Get<TSagaData>(Guid sagaId, ISynchronizedStorageSession session, ContextBag context, CancellationToken cancellationToken = default) where TSagaData : class, IContainSagaData =>
            GetSagaData<TSagaData>(idElementName, sagaId, session, cancellationToken);

        public Task<TSagaData> Get<TSagaData>(string propertyName, object propertyValue, ISynchronizedStorageSession session, ContextBag context, CancellationToken cancellationToken = default) where TSagaData : class, IContainSagaData =>
            GetSagaData<TSagaData>(typeof(TSagaData).GetElementName(propertyName), propertyValue, session, cancellationToken);

        public async Task Complete(IContainSagaData sagaData, ISynchronizedStorageSession session, ContextBag context, CancellationToken cancellationToken = default)
        {
            var storageSession = ((SynchronizedStorageSession)session).Session;
            var sagaDataType = sagaData.GetType();

            var version = storageSession.RetrieveVersion(sagaDataType);

            var result = await storageSession.DeleteOneAsync(sagaDataType, filterBuilder.Eq(idElementName, sagaData.Id) & filterBuilder.Eq(versionElementName, version), cancellationToken).ConfigureAwait(false);

            if (result.DeletedCount != 1)
            {
                throw new Exception("Saga can't be completed because it was updated by another process.");
            }
        }

        async Task<TSagaData> GetSagaData<TSagaData>(string elementName, object elementValue, ISynchronizedStorageSession session, CancellationToken cancellationToken)
        {
            var storageSession = ((SynchronizedStorageSession)session).Session;
            var elementValueBsonRepresentation = elementValue is Guid guid
                ? new BsonBinaryData(guid, GuidRepresentation.Standard)
                : BsonValue.Create(elementValue);

            var document = await storageSession.Find<TSagaData>(new BsonDocument(elementName, elementValueBsonRepresentation), cancellationToken).ConfigureAwait(false);

            if (document != null)
            {
                var version = document.GetValue(versionElementName);
                storageSession.StoreVersion<TSagaData>(version.AsInt32);

                return BsonSerializer.Deserialize<TSagaData>(document);
            }

            return default;
        }

        readonly string versionElementName;
        readonly FilterDefinitionBuilder<BsonDocument> filterBuilder = Builders<BsonDocument>.Filter;

        const string idElementName = "_id";
        internal const string DefaultVersionElementName = "_version";
    }
}