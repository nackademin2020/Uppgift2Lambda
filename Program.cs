// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Provisioning.Client;
using Microsoft.Azure.Devices.Provisioning.Client.Transport;
using Microsoft.Azure.Devices.Shared;
using System;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using Newtonsoft.Json;
using System.Security.Cryptography.X509Certificates;

namespace X509CertificateSimulatedDevice
{
    class Program
    {
        // Azure Device Provisioning Service (DPS) Global Device Endpoint.
        private const string GlobalDeviceEndpoint = "global.azure-devices-provisioning.net";

        // Azure Device Provisioning Service (DPS) ID Scope.
        private static string dpsIdScope = "";

        // Certificate (PFX) File Name
        private static string s_certificateFileName = "";

        // Certificate (PFX) Password. Better to use a Hardware Security Module for production devices.
        private static string s_certificatePassword = "1234";

        public static int Main(string[] args)
        {
            X509Certificate2 certificate = LoadProvisioningCertificate();

            using (var security = new SecurityProviderX509Certificate(certificate))
            {
                using (var transport = new ProvisioningTransportHandlerAmqp(TransportFallbackType.TcpOnly))
                {
                    ProvisioningDeviceClient provClient =
                        ProvisioningDeviceClient.Create(GlobalDeviceEndpoint, dpsIdScope, security, transport);

                    var provisioningDeviceLogic = new ProvisioningDeviceLogic(provClient, security);
                    provisioningDeviceLogic.RunAsync().GetAwaiter().GetResult();
                }
            }

            return 0;
        }

        private static X509Certificate2 LoadProvisioningCertificate()
        {
            var certificateCollection = new X509Certificate2Collection();
            certificateCollection.Import(s_certificateFileName, s_certificatePassword, X509KeyStorageFlags.UserKeySet);

            X509Certificate2 certificate = null;

            foreach (X509Certificate2 element in certificateCollection)
            {
                Console.WriteLine($"Found certificate: {element?.Thumbprint} {element?.Subject}; PrivateKey: {element?.HasPrivateKey}");
                if (certificate == null && element.HasPrivateKey)
                {
                    certificate = element;
                }
                else
                {
                    element.Dispose();
                }
            }

            if (certificate == null)
            {
                throw new FileNotFoundException($"{s_certificateFileName} did not contain any certificate with a private key.");
            }

            Console.WriteLine($"Using certificate {certificate.Thumbprint} {certificate.Subject}");
            return certificate;
        }
    }

    // The ProvisioningDeviceLogic class contains the device logic to read from the simulated Device Sensors, and send Device-to-Cloud
    // messages to the Azure IoT Hub. It also contains the code that updates the device with changes to the device twin properties.
    public class ProvisioningDeviceLogic
    {
        readonly ProvisioningDeviceClient _provClient;
        readonly SecurityProvider _security;
        DeviceClient s_deviceClient;

        

        public ProvisioningDeviceLogic(ProvisioningDeviceClient provisioningDeviceClient, SecurityProvider security)
        {
            _provClient = provisioningDeviceClient;
            _security = security;
        }

        private static void colorMessage(string text, ConsoleColor clr)
        {
            Console.ForegroundColor = clr;
            Console.WriteLine(text);
            Console.ResetColor();
        }
        private static void greenMessage(string text)
        {
            colorMessage(text, ConsoleColor.Green);
        }

        private static void redMessage(string text)
        {
            colorMessage(text, ConsoleColor.Red);
        }

        private static void whiteMessage(string text)
        {
            colorMessage(text, ConsoleColor.White);
        }

        public async Task RunAsync()
        {
            colorMessage($"\nRegistrationID = {_security.GetRegistrationID()}", ConsoleColor.Yellow);

            // Register the Device with DPS.
            whiteMessage("ProvisioningClient RegisterAsync . . . ");
            DeviceRegistrationResult result = await _provClient.RegisterAsync().ConfigureAwait(false);

            if (result.Status == ProvisioningRegistrationStatusType.Assigned)
            {
                greenMessage($"Device Registration Status: {result.Status}");
                greenMessage($"ProvisioningClient AssignedHub: {result.AssignedHub}; DeviceID: {result.DeviceId}");
            }
            else
            {
                redMessage($"Device Registration Status: {result.Status}");
                throw new Exception($"DeviceRegistrationResult.Status is NOT 'Assigned'");
            }

            // Create x509 DeviceClient Authentication.
            whiteMessage("Creating X509 DeviceClient authentication.");
            var auth = new DeviceAuthenticationWithX509Certificate(result.DeviceId, (_security as SecurityProviderX509).GetAuthenticationCertificate());

            whiteMessage("Simulated Device. Ctrl-C to exit.");
            using (s_deviceClient = DeviceClient.Create(result.AssignedHub, auth, TransportType.Amqp))
            {
                // Explicitly open DeviceClient to communicate with Azure IoT Hub.
                whiteMessage("DeviceClient OpenAsync.");
                await s_deviceClient.OpenAsync().ConfigureAwait(false);

                // Start reading and sending device telemetry.
                colorMessage("\nStart reading and sending device telemetry...\n", ConsoleColor.Yellow);
                await SendDeviceToCloudMessagesAsync(s_deviceClient);
                await SendDeviceToCloudLogMessagesAsync(s_deviceClient);

                // Explicitly close DeviceClient.
                whiteMessage("DeviceClient CloseAsync.");
                await s_deviceClient.CloseAsync().ConfigureAwait(false);
            }
        }


        private static async Task SendDeviceToCloudMessagesAsync(DeviceClient s_deviceClient)
        {
            var sensor = new EnvironmentSensor();

            while (true)
            {
                var currentTemperature = sensor.ReadTemperature();
                var currentHumidity = sensor.ReadHumidity();
                var currentPressure = sensor.ReadPressure();
                var currentLocation = sensor.ReadLocation();

                var messageString = CreateMessageString(currentTemperature,
                                                        currentHumidity,
                                                        currentPressure,
                                                        currentLocation);

                var message = new Message(Encoding.ASCII.GetBytes(messageString));

                // Add a custom application property to the message.
                // An IoT hub can filter on these properties without access to the message body.
                message.Properties.Add("SensorType", "Stelemetry");

                // Send the telemetry message
               await s_deviceClient.SendEventAsync(message);
                Console.WriteLine("{0} > Sending message: {1}", DateTime.Now, messageString);

                // Delay before next Telemetry reading
                await Task.Delay(1000);
            }
        }

        private static async Task SendDeviceToCloudLogMessagesAsync(DeviceClient s_deviceClient)
        {
            var sensor = new EnvironmentSensor();

            while (true)
            {
                var currentTemperature = sensor.ReadTemperature();
                var currentHumidity = sensor.ReadHumidity();
                var currentPressure = sensor.ReadPressure();
                var currentLocation = sensor.ReadLocation();

                var messageString = CreateMessageString(currentTemperature,
                                                        currentHumidity,
                                                        currentPressure,
                                                        currentLocation);

                var message = new Message(Encoding.ASCII.GetBytes(messageString));

                // Add a custom application property to the message.
                // An IoT hub can filter on these properties without access to the message body.
                message.Properties.Add("SensorType", "Slog");

                // Send the telemetry message
                await s_deviceClient.SendEventAsync(message);
                Console.WriteLine("{0} > Sending message: {1}", DateTime.Now, messageString);

                // Delay before next Telemetry reading
                await Task.Delay(1000);
            }
        }

        private static string CreateMessageString(double temperature, double humidity, double pressure, EnvironmentSensor.Location location)
        {
            // Create an anonymous object that matches the data structure we wish to send
            var telemetryDataPoint = new
            {
                temperature = temperature,
                humidity = humidity,
                pressure = pressure,
                latitude = location.Latitude,
                longitude = location.Longitude
            };
            var messageString = JsonConvert.SerializeObject(telemetryDataPoint);

            // Create a JSON string from the anonymous object
            return JsonConvert.SerializeObject(telemetryDataPoint);
        }
    }

    internal class EnvironmentSensor
    {
        // Initial telemetry values
        double minTemperature = 20;
        double minHumidity = 60;
        double minPressure = 1013.25;
        double minLatitude = 39.810492;
        double minLongitude = -98.556061;
        Random rand = new Random();

        internal class Location
        {
            internal double Latitude;
            internal double Longitude;
        }

        internal double ReadTemperature()
        {
            return minTemperature + rand.NextDouble() * 15;
        }
        internal double ReadHumidity()
        {
            return minHumidity + rand.NextDouble() * 20;
        }
        internal double ReadPressure()
        {
            return minPressure + rand.NextDouble() * 12;
        }
        internal Location ReadLocation()
        {
            return new Location { Latitude = minLatitude + rand.NextDouble() * 0.5, Longitude = minLongitude + rand.NextDouble() * 0.5 };
        }
    }
}