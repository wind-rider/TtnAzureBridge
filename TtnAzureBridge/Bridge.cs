﻿using Microsoft.Azure.Devices;
using Newtonsoft.Json;
using System;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;

namespace TtnAzureBridge
{
    public class Bridge
    {
        private DeviceClientList _deviceClientList;

        private RegistryManager _registryManager;

        private MqttClient _mqttClient;

        private readonly int _removeDevicesAfterMinutes;

        private readonly string _applicationEui;

        private readonly string _deviceKeyKind;

        private readonly string _brokerHostName;

        private readonly string _exitOnConnectionClosed;

        private readonly string _topic;

        private readonly ushort? _keepAlivePeriod;

        private readonly string _applicationAccessKey;

        private readonly string _iotHub;

        private readonly string _iotHubName;

        public Bridge(int removeDevicesAfterMinutes, string applicationEui, string iotHub, string iotHubName, string topic, string brokerHostName, ushort? keepAlivePeriod, string applicationAccessKey, string deviceKeyKind, string exitOnConnectionClosed)
        {
            _removeDevicesAfterMinutes = removeDevicesAfterMinutes;

            _applicationEui = applicationEui;

            _deviceKeyKind = deviceKeyKind;

            _brokerHostName = brokerHostName;

            _exitOnConnectionClosed = exitOnConnectionClosed;

            _topic = topic;

            _keepAlivePeriod = keepAlivePeriod;

            _applicationAccessKey = applicationAccessKey;

            _iotHub = iotHub;

            _iotHubName = iotHubName;
        }

        public void Start()
        {
            ConstructDeviceList();

            ConstructIoTHubInfrastructure();

            StartMqttConnection();
        }

        /// <summary>
        /// Construct a device list for unique device handling
        /// </summary>
        private void ConstructDeviceList()
        {
            _deviceClientList = new DeviceClientList(_iotHubName, _removeDevicesAfterMinutes);

            _deviceClientList.DeviceRemoved += (sender, message) =>
            {
                WriteLine(message);
            };

            _deviceClientList.IoTHubMessageReceived += (sender, message) =>
            {
                Write("IoT Hub Downlink");

                var payload = Convert.ToBase64String(message.Bytes);
                var jsonMessage = "{\"payload\":\"" + payload + "\", \"port\": 1, \"ttl\": \"1h\"}";

                Write($"; Uploaded: {jsonMessage}");

                var encoding = Encoding.UTF8;
                var bytes = encoding.GetBytes(jsonMessage);

                var mqttResult =
                 _mqttClient.Publish(
                     $"{_applicationEui}/devices/{message.DeviceId}/down",
                     bytes,
                     MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE,
                     false);

                WriteLine($" - Id {mqttResult}");
            };
        }

        /// <summary>
        /// Connect to Azure IoT Hub
        /// </summary>
        private void ConstructIoTHubInfrastructure()
        {
            _registryManager = RegistryManager.CreateFromConnectionString(_iotHub);

            Write($"time {DateTime.Now} -> ");

            WriteLine($"IoT Hub {_iotHubName} connected");
        }

        /// <summary>
        /// Open MQTT connection
        /// </summary>
        private void StartMqttConnection()
        {
            _mqttClient = new MqttClient(_brokerHostName);

            _mqttClient.Subscribe(
                new[] { _topic },
                new[] { MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE });

            _mqttClient.ConnectionClosed += Client_ConnectionClosed;

            _mqttClient.MqttMsgSubscribed += Client_MqttMsgSubscribed;

            _mqttClient.MqttMsgPublishReceived += Client_MqttMsgPublishReceived;

            _mqttClient.MqttMsgPublished += _mqttClient_MqttMsgPublished;

            byte response;

            if (_keepAlivePeriod.HasValue)
            {
                response = _mqttClient.Connect(
                    Guid.NewGuid().ToString(),
                    _applicationEui,
                    _applicationAccessKey,
                    true,
                    _keepAlivePeriod.Value);

                WriteLine($"MQTT KeepAlivePeriod is {_keepAlivePeriod}");
            }
            else
            {
                response = _mqttClient.Connect(
                    Guid.NewGuid().ToString(),
                    _applicationEui,
                    _applicationAccessKey);
            }

            if (response != 0)
            {
                WriteLine("Mqtt connection failed. Check TTN credentials.");
            }
        }

        /// <summary>
        /// Log MQTT publish
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void _mqttClient_MqttMsgPublished(object sender, MqttMsgPublishedEventArgs e)
        {
            WriteLine($"MQTT handling downlink Id {e.MessageId} published: {e.IsPublished}");
        }

        /// <summary>
        /// Publish MQTT message
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void Client_MqttMsgPublishReceived(object sender, MqttMsgPublishEventArgs e)
        {
            // Get id of device

            var deviceId = e.Topic.Split('/')[2];

            if (e.Message.Length < 200)
            {
                // ignore rogue messages

                //WriteLine($"Message length {e.Message.Length} from {deviceId} ignored");

                return;
            }

            WriteLine("MQTT handling uplink");

            // Create or get device

            var device = await AddDeviceAsync(deviceId);

            if (device.Status != DeviceStatus.Enabled)
            {
                WriteLine($"Device {deviceId} disabled");

                return;
            }

            // Convert message to json
            //"{\"port\":1,\"counter\":504,\"payload_raw\":\"+QA=\",\"payload_fields\":{\"errorCode\":0,\"numberOfCycles\":249},\"metadata\":{\"time\":\"2017-01-10T23:31:06.087189682Z\",\"frequency\":868.1,\"modulation\":\"LORA\",\"data_rate\":\"SF7BW125\",\"coding_rate\":\"4/5\",\"gateways\":[{\"gtw_id\":\"eui-b827ebffffc19ca8\",\"gtw_trusted\":true,\"timestamp\":3771642998,\"time\":\"1754-08-30T22:43:41.128654848Z\",\"channel\":0,\"rssi\":-80,\"snr\":9,\"latitude\":51.46018,\"longitude\":5.61902,\"altitude\":10}]}}");

            var jsonText = Encoding.UTF8.GetString(e.Message);
            var jsonObject = JObject.Parse(jsonText);

            var counter = jsonObject.SelectToken("counter").ToString();
            var deviceMessage = jsonObject.SelectToken("payload_fields").ToString();

            var gatewayEui = jsonObject.SelectToken("metadata.gateways[0].gtw_id").ToString();
            var latitude = jsonObject.SelectToken("metadata.gateways[0].latitude").ToString();
            var longitude = jsonObject.SelectToken("metadata.gateways[0].longitude").ToString();
            var rssi = jsonObject.SelectToken("metadata.gateways[0].rssi").ToString();
            var frequency = jsonObject.SelectToken("metadata.frequency").ToString();

            // construct message for IoT Hub

            dynamic iotHubMessage = JsonConvert.DeserializeObject(deviceMessage);

            var iotHubMessageString = JsonConvert.SerializeObject(iotHubMessage);

            Write($"Message received ({counter}/{deviceId}/{gatewayEui}/{latitude}/{longitude}/{frequency}/{rssi}): {iotHubMessageString}");

            // create device client

            var key = (_deviceKeyKind == "Primary")
                ? device.Authentication.SymmetricKey.PrimaryKey
                : device.Authentication.SymmetricKey.SecondaryKey;

            var deviceClient = _deviceClientList.GetDeviceClient(deviceId, key);

            // send message

            var message = new Microsoft.Azure.Devices.Client.Message(Encoding.UTF8.GetBytes(iotHubMessageString));

            await deviceClient.SendEventAsync(message);

            WriteLine("-IoT Hub message sent");
        }

        /// <summary>
        /// Log MQTT client subscription
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Client_MqttMsgSubscribed(object sender, MqttMsgSubscribedEventArgs e)
        {
            WriteLine($"MQTT subscribed to {_applicationEui} on {_brokerHostName}");
        }

        private void Client_ConnectionClosed(object sender, EventArgs e)
        {
            Write($"time {DateTime.Now} -> ");

            Write("MQTT connection closed.");

            if (_exitOnConnectionClosed.ToUpper() == "TRUE")
            {
                WriteLine(" Exit for restart.");

                Environment.Exit(1);
            }

            Write(" No exit.");
        }

        /// <summary>
        /// Add a device to the IoT Hub registry
        /// </summary>
        /// <param name="deviceId"></param>
        /// <returns>Microsoft.Azure.Devices.Device</returns>
        private async Task<Device> AddDeviceAsync(string deviceId)
        {
            Device device;

            {
                try
                {
                    device = await _registryManager.AddDeviceAsync(new Device(deviceId));

                    WriteLine($"Device {deviceId} added");
                }
                catch (Microsoft.Azure.Devices.Common.Exceptions.DeviceAlreadyExistsException)
                {
                    // there are actually two different DeviceAlreadyExistsException exceptions. We react on the right one.

                    device = await _registryManager.GetDeviceAsync(deviceId);
                }
            }

            return device;
        }

        private void Write(string message)
        {
            Notified?.Invoke(this, message);
        }

        private void WriteLine(string message)
        {
            LineNotified?.Invoke(this, message);
        }

        public event EventHandler<string> Notified;

        public event EventHandler<string> LineNotified;
    }
}