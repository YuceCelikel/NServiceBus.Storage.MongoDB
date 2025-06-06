﻿namespace NServiceBus.Persistence.AcceptanceTests.SagaDataStorage;

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AcceptanceTesting;
using NServiceBus.AcceptanceTests;
using NServiceBus.AcceptanceTests.EndpointTemplates;
using NUnit.Framework;

[TestFixture]
public class When_storing_saga_with_high_contention : NServiceBusAcceptanceTest
{
    [Test]
    public async Task Should_succeed_without_retries()
    {
        var context = await Scenario.Define<Context>()
            .WithEndpoint<SagaEndpoint>(b =>
                b.When(session => session.SendLocal(new StartSaga { SomeId = Guid.NewGuid() })))
            .Done(c => c.Done)
            .Run();

        Assert.Multiple(() =>
        {
            Assert.That(context.NumberOfRetries, Is.EqualTo(0));
            Assert.That(context.MessagesSent, Is.True);
            Assert.That(context.SagaStarted, Is.True);
        });
    }

    public class Context : ScenarioContext
    {
        long numberOfRetries;
        public bool Done { get; set; }
        public bool SagaStarted { get; set; }
        public bool MessagesSent { get; set; }
        public int HitCount { get; set; }

        public Stopwatch Watch { get; } = new Stopwatch();

        public TimeSpan Elapsed => Watch.Elapsed;

        public int NumberOfMessages { get; } = 20;

        public long NumberOfRetries => Interlocked.Read(ref numberOfRetries);

        public void IncrementNumberOfRetries()
        {
            Interlocked.Increment(ref numberOfRetries);
        }
    }

    class SagaEndpoint : EndpointConfigurationBuilder
    {
        public SagaEndpoint()
        {
            EndpointSetup<DefaultServer, Context>((b, c) =>
            {
                b.LimitMessageProcessingConcurrencyTo(c.NumberOfMessages);
                var recoverability = b.Recoverability();
                recoverability.Immediate(s =>
                {
                    s.OnMessageBeingRetried((m, _) =>
                    {
                        c.IncrementNumberOfRetries();
                        return Task.CompletedTask;
                    });
                    s.NumberOfRetries(c.NumberOfMessages);
                });
                recoverability.Delayed(s => s.NumberOfRetries(0));
            });
        }

        class HighContentionSaga : Saga<HighContentionSaga.HighContentionSagaData>, IAmStartedByMessages<StartSaga>,
            IHandleMessages<AdditionalMessage>
        {
            Context testContext;

            public HighContentionSaga(Context testContext)
            {
                this.testContext = testContext;
            }

            protected override void ConfigureHowToFindSaga(SagaPropertyMapper<HighContentionSagaData> mapper)
            {
                mapper.ConfigureMapping<StartSaga>(m => m.SomeId).ToSaga(d => d.SomeId);
                mapper.ConfigureMapping<AdditionalMessage>(m => m.SomeId).ToSaga(d => d.SomeId);
            }

            public async Task Handle(StartSaga message, IMessageHandlerContext context)
            {
                Data.SomeId = message.SomeId;
                testContext.Watch.Start();
                testContext.SagaStarted = true;

                await Task.WhenAll(Enumerable.Range(0, testContext.NumberOfMessages).Select(i =>
                    context.SendLocal(new AdditionalMessage { SomeId = message.SomeId })));
                testContext.MessagesSent = true;
            }

            public class HighContentionSagaData : ContainSagaData
            {
                public int Hit { get; set; }
                public Guid SomeId { get; set; }
            }

            public async Task Handle(AdditionalMessage message, IMessageHandlerContext context)
            {
                Data.Hit++;

                if (Data.Hit >= testContext.NumberOfMessages)
                {
                    MarkAsComplete();
                    await context.SendLocal(new DoneSaga { SomeId = message.SomeId, HitCount = Data.Hit });
                }
            }
        }

        class DoneHandler : IHandleMessages<DoneSaga>
        {
            readonly Context testContext;

            public DoneHandler(Context testContext)
            {
                this.testContext = testContext;
            }

            public Task Handle(DoneSaga message, IMessageHandlerContext context)
            {
                testContext.Watch.Stop();
                testContext.HitCount = message.HitCount;
                testContext.Done = true;
                return Task.CompletedTask;
            }
        }
    }

    public class StartSaga : IMessage
    {
        public Guid SomeId { get; set; }
    }

    public class DoneSaga : IMessage
    {
        public Guid SomeId { get; set; }
        public int HitCount { get; set; }
    }

    public class AdditionalMessage : IMessage
    {
        public Guid SomeId { get; set; }
    }
}