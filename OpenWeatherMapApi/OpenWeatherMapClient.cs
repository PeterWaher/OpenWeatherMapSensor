using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Waher.Content;
using Waher.Things;
using Waher.Things.SensorData;

namespace OpenWeatherMapApi
{
	public class OpenWeatherMapClient
	{
		private readonly string apiKey;
		private readonly string location;
		private readonly string country;

		public OpenWeatherMapClient(string ApiKey, string Location, string Country)
		{
			this.apiKey = ApiKey;
			this.location = Location;
			this.country = Country;
		}

		public Task<Field[]> GetData()
		{
			return this.GetData(this.location, this.country);
		}

		public Task<Field[]> GetData(string Location, string Country)
		{
			return GetData(this.apiKey, Location, Country);
		}

		public static async Task<Field[]> GetData(string ApiKey, string Location, string Country)
		{
			Uri Uri = new Uri("http://api.openweathermap.org/data/2.5/weather?q=" + Location + "," + 
				Country + "&units=metric&APPID=" + ApiKey);

			ContentResponse Content = await InternetContent.GetAsync(Uri, new KeyValuePair<string, string>("Accept", "application/json"));
			Content.AssertOk();

			List<Field> Result = new List<Field>();
			DateTime Timestamp = DateTime.Now;
			object Obj = Content.Decoded;

			if (!(Obj is Dictionary<string, object> Response))
				throw new Exception("Unexpected response from API.");

			if (Response.TryGetValue("dt", out Obj) && Obj is int dt)
				Timestamp = JSON.UnixEpoch.AddSeconds(dt);

			if (Response.TryGetValue("name", out Obj) && Obj is string Name)
			{
				Result.Add(new StringField(ThingReference.Empty, Timestamp, "Name", Name,
					FieldType.Identity, FieldQoS.AutomaticReadout));
			}

			if (Response.TryGetValue("id", out Obj))
			{
				Result.Add(new StringField(ThingReference.Empty, Timestamp, "ID", Obj.ToString(),
					FieldType.Identity, FieldQoS.AutomaticReadout));
			}

			if (Response.TryGetValue("timezone", out Obj) && Obj is int TimeZone)
			{
				Result.Add(new QuantityField(ThingReference.Empty, Timestamp, "Time Zone", TimeZone / 3600.0, 2, "h",
					FieldType.Identity, FieldQoS.AutomaticReadout));
			}

			if (Response.TryGetValue("visibility", out Obj) && Obj is int Visibility)
			{
				Result.Add(new QuantityField(ThingReference.Empty, Timestamp, "Visibility", Visibility, 0, "m",
					FieldType.Momentary, FieldQoS.AutomaticReadout));
			}

			if (Response.TryGetValue("coord", out Obj) && Obj is Dictionary<string, object> Coord)
			{
				if (Coord.TryGetValue("lon", out Obj) && Obj is double Lon)
				{
					Result.Add(new QuantityField(ThingReference.Empty, Timestamp, "Longitude", Lon, 2, "°",
						  FieldType.Identity, FieldQoS.AutomaticReadout));
				}

				if (Coord.TryGetValue("lat", out Obj) && Obj is double Lat)
				{
					Result.Add(new QuantityField(ThingReference.Empty, Timestamp, "Latitude", Lat, 2, "°",
						FieldType.Identity, FieldQoS.AutomaticReadout));
				}
			}

			if (Response.TryGetValue("main", out Obj) && Obj is Dictionary<string, object> Main)
			{
				if (Main.TryGetValue("temp", out Obj) && Obj is double Temp)
				{
					Result.Add(new QuantityField(ThingReference.Empty, Timestamp, "Temperature", Temp, 2, "°C",
						FieldType.Momentary, FieldQoS.AutomaticReadout));
				}

				if (Main.TryGetValue("feels_like", out Obj) && Obj is double FeelsLike)
				{
					Result.Add(new QuantityField(ThingReference.Empty, Timestamp, "Feels Like", FeelsLike, 2, "°C",
					FieldType.Computed, FieldQoS.AutomaticReadout));
				}

				if (Main.TryGetValue("temp_min", out Obj) && Obj is double TempMin)
				{
					Result.Add(new QuantityField(ThingReference.Empty, Timestamp, "Temperature, Min", TempMin, 2, "°C",
				FieldType.Peak, FieldQoS.AutomaticReadout));
				}

				if (Main.TryGetValue("temp_max", out Obj) && Obj is double TempMax)
				{
					Result.Add(new QuantityField(ThingReference.Empty, Timestamp, "Temperature, Max", TempMax, 2, "°C",
						FieldType.Peak, FieldQoS.AutomaticReadout));
				}

				if (Main.TryGetValue("pressure", out Obj) && Obj is int Pressure)
				{
					Result.Add(new QuantityField(ThingReference.Empty, Timestamp, "Pressure", Pressure, 0, "hPa",
						FieldType.Momentary, FieldQoS.AutomaticReadout));
				}

				if (Main.TryGetValue("humidity", out Obj) && Obj is int Humidity)
				{
					Result.Add(new QuantityField(ThingReference.Empty, Timestamp, "Humidity", Humidity, 0, "%",
						FieldType.Momentary, FieldQoS.AutomaticReadout));
				}
			}

			if (Response.TryGetValue("wind", out Obj) && Obj is Dictionary<string, object> Wind)
			{
				if (Wind.TryGetValue("speed", out Obj) && Obj is double Speed)
				{
					Result.Add(new QuantityField(ThingReference.Empty, Timestamp, "Wind, Speed", Speed, 1, "m/s",
						FieldType.Momentary, FieldQoS.AutomaticReadout));
				}

				if (Wind.TryGetValue("deg", out Obj) && Obj is int Deg)
				{
					Result.Add(new QuantityField(ThingReference.Empty, Timestamp, "Wind, Direction", Deg, 0, "°",
						FieldType.Momentary, FieldQoS.AutomaticReadout));
				}
			}

			if (Response.TryGetValue("clouds", out Obj) && Obj is Dictionary<string, object> Clouds)
			{
				if (Clouds.TryGetValue("all", out Obj) && Obj is int All)
				{
					Result.Add(new QuantityField(ThingReference.Empty, Timestamp, "Cloudiness", All, 0, "%",
						FieldType.Momentary, FieldQoS.AutomaticReadout));
				}
			}

			if (Response.TryGetValue("sys", out Obj) && Obj is Dictionary<string, object> Sys)
			{
				if (Sys.TryGetValue("id", out Obj) && Obj is int WeatherId)
				{
					Result.Add(new Int32Field(ThingReference.Empty, Timestamp, "Weather, ID", WeatherId,
						FieldType.Momentary, FieldQoS.AutomaticReadout));
				}

				if (Sys.TryGetValue("country", out Obj) && Obj is string Country2)
				{
					Result.Add(new StringField(ThingReference.Empty, Timestamp, "Country", Country2,
						FieldType.Identity, FieldQoS.AutomaticReadout));
				}

				if (Sys.TryGetValue("sunrise", out Obj) && Obj is int Sunrise)
				{
					Result.Add(new DateTimeField(ThingReference.Empty, Timestamp, "Sunrise", JSON.UnixEpoch.AddSeconds(Sunrise),
						FieldType.Momentary, FieldQoS.AutomaticReadout));
				}

				if (Sys.TryGetValue("sunset", out Obj) && Obj is int Sunset)
				{
					Result.Add(new DateTimeField(ThingReference.Empty, Timestamp, "Sunset", JSON.UnixEpoch.AddSeconds(Sunset),
						FieldType.Momentary, FieldQoS.AutomaticReadout));
				}
			}

			if (Response.TryGetValue("weather", out Obj) &&
				Obj is Array WeatherArray &&
				WeatherArray.Length == 1 &&
				WeatherArray.GetValue(0) is Dictionary<string, object> Weather)
			{
				if (Weather.TryGetValue("main", out Obj) && Obj is string Main2)
				{
					Result.Add(new StringField(ThingReference.Empty, Timestamp, "Weather", Main2,
						FieldType.Momentary, FieldQoS.AutomaticReadout));
				}

				if (Weather.TryGetValue("description", out Obj) && Obj is string Description)
				{
					Result.Add(new StringField(ThingReference.Empty, Timestamp, "Weather, Description", Description,
						FieldType.Momentary, FieldQoS.AutomaticReadout));
				}

				if (Weather.TryGetValue("icon", out Obj) && Obj is string Icon)
				{
					Result.Add(new StringField(ThingReference.Empty, Timestamp, "Weather, Icon",
						"http://openweathermap.org/img/wn/" + Icon + "@2x.png",
						FieldType.Momentary, FieldQoS.AutomaticReadout));
				}
			}

			return Result.ToArray();
		}
	}
}
