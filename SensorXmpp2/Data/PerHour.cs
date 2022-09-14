using Waher.Persistence.Attributes;

namespace SensorXmpp.Data
{
	/// <summary>
	/// Historic record, sampled every hour
	/// </summary>
	[CollectionName("HistoryPerHour")]
	public class PerHour : HistoricRecordWithPeaks
	{
	}
}
