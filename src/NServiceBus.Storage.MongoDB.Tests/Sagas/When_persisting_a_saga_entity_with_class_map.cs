﻿namespace NServiceBus.Storage.MongoDB.Tests;

using System;
using System.Threading.Tasks;
using global::MongoDB.Bson;
using global::MongoDB.Bson.Serialization;
using global::MongoDB.Bson.Serialization.Serializers;
using NUnit.Framework;
using Sagas;

public class When_persisting_a_saga_entity_with_class_map : SagaPersisterTests
{
    [Test]
    public async Task Should_support_full_saga_lifecycle()
    {
        var classMap = new BsonClassMap(typeof(CustomSagaData));
        classMap.MapIdProperty(nameof(CustomSagaData.Id))
            .SetElementName("_id")
            .SetSerializer(new GuidSerializer(GuidRepresentation.CSharpLegacy));
        classMap.MapProperty(nameof(CustomSagaData.Originator));
        classMap.MapProperty(nameof(CustomSagaData.OriginalMessageId));
        classMap.SetIgnoreExtraElements(true);

        BsonClassMap.RegisterClassMap(classMap);

        var entity = new CustomSagaData
        {
            Id = Guid.NewGuid(),
            OriginalMessageId = Guid.NewGuid().ToString(),
            Originator = "SomeOriginator"
        };

        var insertContextBag = configuration.GetContextBagForSagaStorage();
        using (var insertSession = configuration.SessionFactory())
        {
            await insertSession.Open(insertContextBag);
            var correlationProperty = new SagaCorrelationProperty(nameof(IContainSagaData.Id), entity.Id);

            await configuration.SagaStorage.Save(entity, correlationProperty, insertSession, insertContextBag);
            await insertSession.CompleteAsync();
        }

        var savedEntity = await GetById<CustomSagaData>(entity.Id).ConfigureAwait(false);

        var updateContextBag = configuration.GetContextBagForSagaStorage();
        using (var updateSession = configuration.SessionFactory())
        {
            await updateSession.Open(updateContextBag);

            _ = await configuration.SagaStorage.Get<CustomSagaData>(nameof(IContainSagaData.Id), entity.Id,
                updateSession, updateContextBag);

            entity.Originator += "Updated";

            await configuration.SagaStorage.Update(entity, updateSession, updateContextBag);
            await updateSession.CompleteAsync();
        }

        var updatedEntity = await GetByProperty<CustomSagaData>(nameof(IContainSagaData.Id), entity.Id)
            .ConfigureAwait(false);

        var completeContextBag = configuration.GetContextBagForSagaStorage();
        using (var completeSession = configuration.SessionFactory())
        {
            await completeSession.Open(completeContextBag);

            _ = await configuration.SagaStorage.Get<CustomSagaData>(entity.Id, completeSession, completeContextBag);

            await configuration.SagaStorage.Complete(entity, completeSession, completeContextBag);
            await completeSession.CompleteAsync();
        }

        var completedEntity = await GetById<CustomSagaData>(entity.Id);

        Assert.Multiple(() =>
        {
            Assert.That(savedEntity, Is.Not.Null);
            Assert.That(updatedEntity, Is.Not.Null);
            Assert.That(updatedEntity.Originator, Is.EqualTo("SomeOriginatorUpdated"));
            Assert.That(completedEntity, Is.Null);
        });
    }

    class CustomSagaData : IContainSagaData
    {
        public Guid Id { get; set; }
        public string Originator { get; set; }
        public string OriginalMessageId { get; set; }
    }
}