using Waher.Persistence.Attributes;

namespace SensorXmpp.Data
{
	/// <summary>
	/// Historic record, sampled every quarter of an hour
	/// </summary>
	[CollectionName("HistoryPerQuarter")]
	public class PerQuarter : HistoricRecordWithPeaks
	{
	}
}
