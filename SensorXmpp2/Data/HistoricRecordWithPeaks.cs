using System;
using Waher.Persistence.Attributes;

namespace SensorXmpp.Data
{
	/// <summary>
	/// Abstract base class of historical records with peak values
	/// </summary>
	public abstract class HistoricRecordWithPeaks : HistoricRecord
	{
		/// <summary>
		/// Minimum Magnitude
		/// </summary>
		[DefaultValue(0.0)]
		public double MinMagnitude { get; set; }

		/// <summary>
		/// Number of decimals for minimum value.
		/// </summary>
		[DefaultValue((byte)0)]
		public byte MinNrDecimals { get; set; }

		/// <summary>
		/// Unit for minimum value
		/// </summary>
		[DefaultValueStringEmpty]
		public string MinUnit { get; set; }

		/// <summary>
		/// Timestamp of Minimum Value
		/// </summary>
		[DefaultValueDateTimeMinValue]
		public DateTime MinTimestamp { get; set; }

		/// <summary>
		/// Maximum Magnitude
		/// </summary>
		[DefaultValue(0.0)]
		public double MaxMagnitude { get; set; }

		/// <summary>
		/// Number of decimals for maximum value.
		/// </summary>
		[DefaultValue((byte)0)]
		public byte MaxNrDecimals { get; set; }

		/// <summary>
		/// Unit for maximum value
		/// </summary>
		[DefaultValueStringEmpty]
		public string MaxUnit { get; set; }

		/// <summary>
		/// Timestamp of Maximum Value
		/// </summary>
		[DefaultValueDateTimeMaxValue]
		public DateTime MaxTimestamp { get; set; }

		/// <summary>
		/// Number of records used for mean valu
		/// </summary>
		public int NrRecords { get; set; }

		/// <summary>
		/// Number of samples used to create the value.
		/// </summary>
		public int NrSamples { get; set; }
	}
}
