﻿#region Licence
/* The MIT License (MIT)
Copyright © 2015 Ian Cooper <ian_hammond_cooper@yahoo.co.uk>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the “Software”), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE. */

#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using FluentAssertions;
using Paramore.Brighter.Core.Tests.CommandProcessors.TestDoubles;
using Polly;
using Polly.Registry;
using Xunit;

namespace Paramore.Brighter.Core.Tests.CommandProcessors
{
    [Collection("CommandProcessor")]
    public class CommandProcessorPostBoxImplicitClearAsyncTests : IDisposable
    {
        private readonly CommandProcessor _commandProcessor;
        private readonly Message _message;
        private readonly Message _message2;
        private readonly FakeOutbox _fakeOutbox;
        private readonly FakeMessageProducer _fakeMessageProducer;

        public CommandProcessorPostBoxImplicitClearAsyncTests()
        {
            var myCommand = new MyCommand{ Value = "Hello World"};

            _fakeOutbox = new FakeOutbox();
            _fakeMessageProducer = new FakeMessageProducer();

            const string topic = "MyCommand";
            _message = new Message(
                new MessageHeader(myCommand.Id, topic, MessageType.MT_COMMAND),
                new MessageBody(JsonSerializer.Serialize(myCommand, JsonSerialisationOptions.Options))
                );

            _message2 = new Message(
                new MessageHeader(Guid.NewGuid(), topic, MessageType.MT_COMMAND),
                new MessageBody(JsonSerializer.Serialize(myCommand, JsonSerialisationOptions.Options))
            );

            var messageMapperRegistry = new MessageMapperRegistry(new SimpleMessageMapperFactory((_) => new MyCommandMessageMapper()));
            messageMapperRegistry.Register<MyCommand, MyCommandMessageMapper>();

            var retryPolicy = Policy
                .Handle<Exception>()
                .RetryAsync();

            var circuitBreakerPolicy = Policy
                .Handle<Exception>()
                .CircuitBreakerAsync(1, TimeSpan.FromMilliseconds(1));

            var producerRegistry = new ProducerRegistry(new Dictionary<string, IAmAMessageProducer>
            {
                { topic, _fakeMessageProducer },
            });

            var policyRegistry = new PolicyRegistry
            {
                { CommandProcessor.RETRYPOLICYASYNC, retryPolicy },
                { CommandProcessor.CIRCUITBREAKERASYNC, circuitBreakerPolicy }
            }; 
            
            IAmAnExternalBusService bus = new ExternalBusServices<Message, CommittableTransaction>(producerRegistry, policyRegistry, _fakeOutbox);

            CommandProcessor.ClearExtServiceBus();
            _commandProcessor = new CommandProcessor(
                new InMemoryRequestContextFactory(),
                policyRegistry,
                messageMapperRegistry,
                bus);
        }

        [Fact]
        public async Task When_Implicit_Clearing_The_PostBox_On_The_Command_Processor_Async()
        {
            await _fakeOutbox.AddAsync(_message);
            await _fakeOutbox.AddAsync(_message2);

            _commandProcessor.ClearAsyncOutbox(1,1);

            for (var i = 1; i <= 10; i++)
            {
                if (_fakeMessageProducer.SentMessages.Count == 1) break;
                await Task.Delay(i * 100);
            }

            _commandProcessor.ClearAsyncOutbox(1, 1);

            //Try again and kick off another background thread
            for (var i = 1; i <= 10; i++)
            {
                if (_fakeMessageProducer.SentMessages.Count == 2)
                    break;
                await Task.Delay(i * 100);
                _commandProcessor.ClearAsyncOutbox(1, 1);
            }

            //_should_send_a_message_via_the_messaging_gateway
            _fakeMessageProducer.MessageWasSent.Should().BeTrue();

            var sentMessage = _fakeMessageProducer.SentMessages.FirstOrDefault(m => m.Id == _message.Id);
            sentMessage.Should().NotBeNull();
            sentMessage.Id.Should().Be(_message.Id);
            sentMessage.Header.Topic.Should().Be(_message.Header.Topic);
            sentMessage.Body.Value.Should().Be(_message.Body.Value);

            var sentMessage2 = _fakeMessageProducer.SentMessages.FirstOrDefault(m => m.Id == _message2.Id);
            sentMessage2.Should().NotBeNull();
            sentMessage2.Id.Should().Be(_message2.Id);
            sentMessage2.Header.Topic.Should().Be(_message2.Header.Topic);
            sentMessage2.Body.Value.Should().Be(_message2.Body.Value);
        }

        public void Dispose()
        {
            CommandProcessor.ClearExtServiceBus();
        }
    }
}
