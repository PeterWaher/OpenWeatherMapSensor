using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

// The Content Dialog item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace SensorXmpp
{
	public sealed partial class OpenWeatherMapDialog : ContentDialog
	{
		public string Key = string.Empty;
		public string Location = string.Empty;
		public string CountryCode = string.Empty;

		public OpenWeatherMapDialog(string Key, string Location, string Country)
		{
			this.InitializeComponent();

			this.ApiKeyInput.Text = this.Key = Key;
			this.LocationInput.Text = this.Location = Location;
			this.CountryInput.Text = this.CountryCode = Country;
		}

		private void ConnectButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
		{
			this.Key = this.ApiKeyInput.Text;
			this.Location = this.LocationInput.Text;
			this.CountryCode = this.CountryInput.Text;
		}

		private void CancelButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
		{
		}
	}
}
