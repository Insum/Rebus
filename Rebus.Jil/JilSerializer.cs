﻿#pragma warning disable 1998
using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using Jil;
using Rebus.Extensions;
using Rebus.Messages;
using Rebus.Serialization;

namespace Rebus.Jil
{
    /// <summary>
    /// Implementation of <see cref="ISerializer"/> that uses Jil to do its thing.
    /// </summary>
    public class JilSerializer : ISerializer
    {
        const string JsonUtf8ContentType = "application/json;charset=utf-8";
        static readonly Encoding Encoding = Encoding.UTF8;

        /// <summary>
        /// Serializes the given <see cref="Message"/> into a <see cref="TransportMessage"/>
        /// </summary>
        public async Task<TransportMessage> Serialize(Message message)
        {
            var body = message.Body;
            var jsonText = JSON.Serialize(body);
            var bytes = Encoding.GetBytes(jsonText);
            var headers = message.Headers.Clone();
            var messageType = body.GetType();
            headers[Headers.Type] = messageType.GetSimpleAssemblyQualifiedName();
            headers[Headers.ContentType] = JsonUtf8ContentType;
            return new TransportMessage(headers, bytes);
        }

        /// <summary>
        /// Deserializes the given <see cref="TransportMessage"/> back into a <see cref="Message"/>
        /// </summary>
        public async Task<Message> Deserialize(TransportMessage transportMessage)
        {
            var contentType = transportMessage.Headers.GetValue(Headers.ContentType);

            if (contentType != JsonUtf8ContentType)
            {
                throw new FormatException(string.Format("Unknown content type: '{0}' - must be '{1}' for the JSON serialier to work", contentType, JsonUtf8ContentType));
            }

            var headers = transportMessage.Headers.Clone();
            var messageType = GetMessageType(headers);
            var bodyString = Encoding.GetString(transportMessage.Body);
            var bodyObject = messageType != null 
                ? JSON.Deserialize(bodyString, messageType)
                : JSON.DeserializeDynamic(bodyString);
            return new Message(headers, bodyObject);
        }

        Type GetMessageType(Dictionary<string, string> headers)
        {
            string messageTypeString;
            if (!headers.TryGetValue(Headers.Type, out messageTypeString))
            {
                return null;
            }

            var type = Type.GetType(messageTypeString, false);

            if (type == null)
            {
                var message = string.Format(
                    "Could not find .NET type matching '{0}' - please be sure that the correct message" +
                    " assembly is available when handling messages", messageTypeString);

                throw new SerializationException(message);
            }

            return type;
        }
    }
}
