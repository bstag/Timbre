using System.Text;
using System.Text.Json.Nodes;

namespace Timbre.Core;

public sealed record PreparedApoUpdate(byte[] ApplyMessage, byte[] RestoreMessage);

// Protocol preparation only. Transport remains disabled until a live exchange is validated.
// A caller must supply the current complete request, so an effect update cannot replace
// the user's other settings with guessed defaults.
public static class SuiteApoRequest
{
    public static PreparedApoUpdate PrepareSurround(JsonArray currentRequest, UsbIdentity device, bool enabled)
    {
        if (currentRequest.Count != 1 || currentRequest[0] is not JsonObject request || request["requestType"]?.ToString() != "13")
            throw new InvalidDataException("A current single-request APO snapshot is required.");
        if (request["SelectedDevice"] is not JsonArray selected || selected.Count != 1 || selected[0] is not JsonObject entry)
            throw new InvalidDataException("The snapshot must address exactly one device.");
        var vid = Convert.ToInt32(device.VendorId, 16).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var pid = Convert.ToInt32(device.ProductId, 16).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var serial = device.InstanceId.Split('\\').Last();
        if (entry["VID"]?.ToString() != vid || entry["PID"]?.ToString() != pid || entry["SerialNumber"]?.ToString() != serial)
            throw new InvalidOperationException("The APO snapshot belongs to another USB device.");
        if (entry["apoParams"] is not JsonObject parameters || parameters["directSoundEnabled"] is not JsonValue oldValue || !oldValue.TryGetValue<bool>(out _))
            throw new InvalidDataException("The snapshot must include a known surround state for restoration.");
        var restored = Encoding.ASCII.GetBytes(currentRequest.ToJsonString());
        var changed = (JsonArray)currentRequest.DeepClone();
        ((JsonObject)changed[0]!["SelectedDevice"]![0]!["apoParams"]!)["directSoundEnabled"] = enabled;
        return new PreparedApoUpdate(Encoding.ASCII.GetBytes(changed.ToJsonString()), restored);
    }
}
