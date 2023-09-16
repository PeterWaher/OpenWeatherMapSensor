using System;
using Waher.Persistence.Attributes;
using Waher.Things.SensorData;

namespace SensorConsole.Data
{
	/// <summary>
	/// Abstract base class of historical records
	/// </summary>
	[Index("FieldName", "Timestamp")]
	[Index("Timestamp", "FieldName")]
	public abstract class HistoricRecord
	{
		/// <summary>
		/// Object ID
		/// </summary>
		[ObjectId]
		public string? ObjectId { get; set; }

		/// <summary>
		/// Field Name
		/// </summary>
		public string FieldName { get; set; } = string.Empty;

		/// <summary>
		/// Timestamp
		/// </summary>
		public DateTime Timestamp { get; set; }

		/// <summary>
		/// Quality of Service
		/// </summary>
		public FieldQoS QoS { get; set; }

		/// <summary>
		/// Magnitude
		/// </summary>
		[DefaultValue(0.0)]
		public double Magnitude { get; set; }

		/// <summary>
		/// Number of decimals
		/// </summary>
		[DefaultValue((byte)0)]
		public byte NrDecimals { get; set; }

		/// <summary>
		/// Unit
		/// </summary>
		[DefaultValueStringEmpty]
		public string Unit { get; set; } = string.Empty;
	}
}
