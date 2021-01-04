using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Waher.Content;

namespace SensorXmpp
{
	public class OpenWeatherMapApi
	{
		private readonly string apiKey;
		private readonly string location;
		private readonly string country;

		public OpenWeatherMapApi(string ApiKey, string Location, string Country)
		{
			this.apiKey = ApiKey;
			this.location = Location;
			this.country = Country;
		}

		public Task GetData()
		{
			return this.GetData(this.location, this.country);
		}

		public Task GetData(string Location, string Country)
		{
			return GetData(this.apiKey, Location, Country);
		}

		public static async Task<Dictionary<string, object>> GetData(string ApiKey, string Location, string Country)
		{
			Uri Uri = new Uri("http://api.openweathermap.org/data/2.5/weather?q=" + Location + "," + Country + "&APPID=" + ApiKey);

			Dictionary<string, object> Result = await InternetContent.GetAsync(Uri, new KeyValuePair<string, string>("Accept", "application/json")) as Dictionary<string, object>;

			return Result;
		}
	}
}
