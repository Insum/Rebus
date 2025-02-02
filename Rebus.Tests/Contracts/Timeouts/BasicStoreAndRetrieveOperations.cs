﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Rebus.Messages;
using Rebus.Time;
using Rebus.Timeouts;

namespace Rebus.Tests.Contracts.Timeouts
{
    public abstract class BasicStoreAndRetrieveOperations<TTimeoutManagerFactory> : FixtureBase where TTimeoutManagerFactory : ITimeoutManagerFactory, new()
    {
        TTimeoutManagerFactory _factory;
        ITimeoutManager _timeoutManager;

        protected override void SetUp()
        {
            _factory = new TTimeoutManagerFactory();
            _timeoutManager = _factory.Create();
        }

        protected override void TearDown()
        {
            _factory.Cleanup();
        }

        [Test]
        public async Task DoesNotLoadAnythingInitially()
        {
            using (var result = await _timeoutManager.GetDueMessages())
            {
                Assert.That(result.Count(), Is.EqualTo(0));
            }
        }

        [Test]
        public async Task IsCapableOfReturningTheMessageBodyAsWell()
        {
            var someTimeInThePast = RebusTime.Now.AddMinutes(-1);

            const string stringBody = "hello there!";

            await _timeoutManager.Defer(someTimeInThePast, HeadersWith("i know u"), Encoding.UTF8.GetBytes(stringBody));

            using (var result = await _timeoutManager.GetDueMessages())
            {
                var dueTimeouts = result.ToList();

                Assert.That(dueTimeouts.Count, Is.EqualTo(1));
                
                var bodyBytes = dueTimeouts[0].Body;
                
                Assert.That(Encoding.UTF8.GetString(bodyBytes), Is.EqualTo(stringBody));
            }
        }

        [Test]
        public async Task ImmediatelyGetsTimeoutWhenItIsDueInThePast()
        {
            var someTimeInThePast = RebusTime.Now.AddMinutes(-1);

            await _timeoutManager.Defer(someTimeInThePast, HeadersWith("i know u"), EmptyBody());

            using (var result = await _timeoutManager.GetDueMessages())
            {
                var dueTimeouts = result.ToList();

                Assert.That(dueTimeouts.Count, Is.EqualTo(1));
                Assert.That(dueTimeouts[0].Headers[Headers.MessageId], Is.EqualTo("i know u"));
            }
        }

        [Test]
        public async Task TimeoutsAreNotRemovedIfTheyAreNotMarkedAsComplete()
        {
            var theFuture = RebusTime.Now.AddMinutes(1);

            await _timeoutManager.Defer(theFuture, HeadersWith("i know u"), EmptyBody());
            
            RebusTimeMachine.FakeIt(theFuture + TimeSpan.FromSeconds(1));

            using (var result = await _timeoutManager.GetDueMessages())
            {
                var dueTimeoutsInTheFuture = result.ToList();
                Assert.That(dueTimeoutsInTheFuture.Count, Is.EqualTo(1));
            }

            using (var result = await _timeoutManager.GetDueMessages())
            {
                var dueTimeoutsInTheFuture = result.ToList();
                Assert.That(dueTimeoutsInTheFuture.Count, Is.EqualTo(1));
            
                // mark as complete
                dueTimeoutsInTheFuture[0].MarkAsCompleted();
            }
            
            using (var result = await _timeoutManager.GetDueMessages())
            {
                var dueTimeoutsInTheFuture = result.ToList();
                Assert.That(dueTimeoutsInTheFuture.Count, Is.EqualTo(0));
            }
        }

        [Test]
        public async Task TimeoutsAreNotReturnedUntilTheyAreActuallyDue()
        {
            var theFuture = DateTimeOffset.Now.AddMinutes(1);
            var evenFurtherIntoTheFuture = DateTimeOffset.Now.AddMinutes(8);

            await _timeoutManager.Defer(theFuture, HeadersWith("i know u"), EmptyBody());
            await _timeoutManager.Defer(evenFurtherIntoTheFuture, HeadersWith("i know u too"), EmptyBody());

            using (var result = await _timeoutManager.GetDueMessages())
            {
                var dueTimeoutsNow = result.ToList();

                Assert.That(dueTimeoutsNow.Count, Is.EqualTo(0));
            }

            RebusTimeMachine.FakeIt(theFuture);

            using (var result = await _timeoutManager.GetDueMessages())
            {
                var dueTimeoutsInTheFuture = result.ToList();
                Assert.That(dueTimeoutsInTheFuture.Count, Is.EqualTo(1));
                Assert.That(dueTimeoutsInTheFuture[0].Headers[Headers.MessageId], Is.EqualTo("i know u"));

                dueTimeoutsInTheFuture[0].MarkAsCompleted();
            }

            RebusTimeMachine.FakeIt(evenFurtherIntoTheFuture);

            using (var result = await _timeoutManager.GetDueMessages())
            {
                var dueTimeoutsFurtherIntoInTheFuture = result.ToList();
                Assert.That(dueTimeoutsFurtherIntoInTheFuture.Count, Is.EqualTo(1));
                Assert.That(dueTimeoutsFurtherIntoInTheFuture[0].Headers[Headers.MessageId], Is.EqualTo("i know u too"));

                dueTimeoutsFurtherIntoInTheFuture[0].MarkAsCompleted();
            }
        }

        static Dictionary<string, string> HeadersWith(string id)
        {
            return new Dictionary<string, string>
            {
                { Headers.MessageId, id }
            };
        }

        static byte[] EmptyBody()
        {
            return new byte[0];
        }
    }
}