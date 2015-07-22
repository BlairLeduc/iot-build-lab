using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Windows.ApplicationModel.Background;
using Windows.Storage;
using Windows.System.Threading;

using build2015_weather_station_task.Sparkfun;

// The Background Application template is documented at http://go.microsoft.com/fwlink/?LinkID=533884&clcid=0x409

namespace build2015_weather_station_task
{
    public sealed partial class StartupTask : IBackgroundTask
    {
        private readonly int i2cReadIntervalSeconds = 2;
        private ThreadPoolTimer i2cTimer;
        private Mutex mutex;
        private string mutexId = "WeatherStation";
        private readonly int port = 50001;
        private ThreadPoolTimer SASTokenRenewTimer;
        private HttpServer server;
        private WeatherShield shield = new WeatherShield();
        private BackgroundTaskDeferral taskDeferral;
        private WeatherData weatherData = new WeatherData();

        // Hard coding guid for sensors. Not an issue for this particular application which is meant for testing and demos
        private List<ConnectTheDotsSensor> sensors = new List<ConnectTheDotsSensor> {
            new ConnectTheDotsSensor("2298a348-e2f9-4438-ab23-82a3930662ab", "Altitude", "m"),
            new ConnectTheDotsSensor("2298a348-e2f9-4438-ab23-82a3930662ac", "Humidity", "%RH"),
            new ConnectTheDotsSensor("2298a348-e2f9-4438-ab23-82a3930662ad", "Pressure", "kPa"),
            new ConnectTheDotsSensor("2298a348-e2f9-4438-ab23-82a3930662ae", "Temperature", "C"),
        };

        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            // Ensure our background task remains running
            taskDeferral = taskInstance.GetDeferral();

            // Mutex will be used to ensure only one thread at a time is talking to the shield / isolated storage
            mutex = new Mutex(false, mutexId);

            // Initialize ConnectTheDots Settings
            localSettings.ServicebusNamespace = "YOURSERVICEBUS-ns";
            localSettings.EventHubName = "ehdevices";
            localSettings.KeyName = "D1";
            localSettings.Key = "YOUR_KEY";
            localSettings.DisplayName = "YOUR_DEVICE_NAME";
            localSettings.Organization = "YOUR_ORGANIZATION_OR_SELF";
            localSettings.Location = "YOUR_LOCATION";

            SaveSettings();

            // Initialize WeatherShield
            await shield.BeginAsync();

            // Create a timer-initiated ThreadPool task to read data from I2C
            i2cTimer = ThreadPoolTimer.CreatePeriodicTimer(PopulateWeatherData, TimeSpan.FromSeconds(i2cReadIntervalSeconds));

            // Start the server
            server = new HttpServer(port);
            var asyncAction = ThreadPool.RunAsync((w) => { server.StartServer(shield, weatherData); });

            // Task cancellation handler, release our deferral there 
            taskInstance.Canceled += OnCanceled;

            // Create a timer-initiated ThreadPool task to renew SAS token regularly
            SASTokenRenewTimer = ThreadPoolTimer.CreatePeriodicTimer(RenewSASToken, TimeSpan.FromMinutes(15));
        }

        private void OnCanceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason)
        {
            // Relinquish our task deferral
            taskDeferral.Complete();
        }

        private void PopulateWeatherData(ThreadPoolTimer timer)
        {
            bool hasMutex = false;

            try
            {
                hasMutex = mutex.WaitOne(1000);
                if (hasMutex)
                {
                    weatherData.TimeStamp = DateTime.Now.ToLocalTime().ToString();

                    shield.BlueLEDPin.Write(Windows.Devices.Gpio.GpioPinValue.High);

                    weatherData.Altitude = shield.Altitude;
                    weatherData.BarometricPressure = shield.Pressure;
                    weatherData.CelsiusTemperature = shield.Temperature;
                    weatherData.FahrenheitTemperature = (weatherData.CelsiusTemperature * 9 / 5) + 32;
                    weatherData.Humidity = shield.Humidity;

                    shield.BlueLEDPin.Write(Windows.Devices.Gpio.GpioPinValue.Low);

                    // Push the WeatherData local/cloud storage (viewable at http://iotbuildlab.azurewebsites.net/)
                    WriteDataToIsolatedStorage();
                    SendDataToConnectTheDots();
                }
            }
            finally
            {
                if (hasMutex)
                {
                    mutex.ReleaseMutex();
                }
            }
        }

        private void RenewSASToken(ThreadPoolTimer timer)
        {
            bool hasMutex = false;

            try
            {
                hasMutex = mutex.WaitOne(1000);
                if (hasMutex)
                {
                    UpdateSASToken();
                }
            }
            finally
            {
                if (hasMutex)
                {
                    mutex.ReleaseMutex();
                }
            }
        }

        private void SendDataToConnectTheDots()
        {
            ConnectTheDotsSensor sensor;
            string time = DateTime.UtcNow.ToString("o");

            // Send the altitude data
            sensor = sensors.Find(item => item.measurename == "Altitude");
            if (sensor != null)
            {
                sensor.value = weatherData.Altitude;
                sensor.timecreated = time;
                sendMessage(sensor.ToJson());
            }

            // Send the humidity data
            sensor = sensors.Find(item => item.measurename == "Humidity");
            if (sensor != null)
            {
                sensor.value = weatherData.Humidity;
                sensor.timecreated = time;
                sendMessage(sensor.ToJson());
            }

            // Sending the pressure data
            sensor = sensors.Find(item => item.measurename == "Pressure");
            if (sensor != null)
            {
                sensor.value = (weatherData.BarometricPressure / 1000);
                sensor.timecreated = time;
                sendMessage(sensor.ToJson());
            }

            // Sending the temperature data
            sensor = sensors.Find(item => item.measurename == "Temperature");
            if (sensor != null)
            {
                sensor.value = weatherData.CelsiusTemperature;
                sensor.timecreated = time;
                sendMessage(sensor.ToJson());
            }
        }

        async private void WriteDataToIsolatedStorage()
        {
            // We have exlusive access to the mutex so can safely wipe the transfer file
            Windows.Globalization.DateTimeFormatting.DateTimeFormatter formatter = new Windows.Globalization.DateTimeFormatting.DateTimeFormatter("longtime");
            StorageFolder localFolder = ApplicationData.Current.LocalFolder;
            StorageFile transferFile = await localFolder.CreateFileAsync("DataFile.txt", CreationCollisionOption.ReplaceExisting);

            using (var stream = await transferFile.OpenStreamForWriteAsync())
            {
                StreamWriter writer = new StreamWriter(stream);

                writer.WriteLine(weatherData.TimeStamp);
                writer.WriteLine(weatherData.Altitude.ToString());
                writer.WriteLine(weatherData.BarometricPressure.ToString());
                writer.WriteLine(weatherData.CelsiusTemperature.ToString());
                writer.WriteLine(weatherData.FahrenheitTemperature.ToString());
                writer.WriteLine(weatherData.Humidity.ToString());
                writer.Flush();
            }
        }
    }
}
