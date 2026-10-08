using EposControl.Core;
using System.Text;
using System.Text.Json.Nodes;



internal static class ExistingTests { public static void Run(TestSuite suite) {
void Check(bool condition, string name) => suite.Case(name, () => TestSuite.Assert(condition));
void Reject(Action action, string name) => suite.Case(name, () => TestSuite.Reject(action));
var demo = new DemoAudioBackend();
var endpoints = demo.Discover(); var mic = endpoints.First(); var playback = endpoints.First(e => e.Direction == AudioDirection.Playback);
var catalog = new DeviceCatalog([new("1395", "009F", "B20", "CMedia", true, true, false), new("1395", "0098", "GSX 300", "Boston", true, true, true)]);
Check(catalog.Features(mic).All(f => f.Name != "7.1 surround"), "Microphone does not expose a playback surround feature");
Check(catalog.Features(playback).Any(f => f.Name == "7.1 surround" && f.State == FeatureState.PendingValidation), "GSX surround stays unavailable until validated");
var unknown = mic with { Usb = new("1395", "FFFF", "UNKNOWN"), Name = "New EPOS device" };
Check(DeviceCatalog.IsEpos(unknown), "Unmapped EPOS model is discovered");
Check(catalog.Features(unknown).Single(f => f.Name == "Sidetone").State == FeatureState.Unknown, "Unmapped model does not inherit B20 commands");
Check(!DeviceCatalog.IsEpos(mic with { Usb = null, Name = "Microphone (EPOS B20) (Elgato Virtual Audio)", AdapterName = "Elgato Virtual Audio" }), "Virtual names do not impersonate physical EPOS hardware");
Reject(() => demo.Apply(mic.Id, float.NaN, false), "Reject NaN level");
Reject(() => demo.Apply(mic.Id, 1.2f, false), "Reject out-of-range level");
var scratch = Path.Combine(Path.GetTempPath(), "epos-control-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try {
    var store = new ProfileStore(Path.Combine(scratch, "profiles.json"));
    var profile = new AudioProfile("Everyday", mic.Id, mic.ProfileIdentity, mic.Direction, .47f, true);
    store.Save(profile); store.Save(profile with { Name = "everyday", Level = .52f });
    Check(store.Load().Count == 1 && store.Load()[0].Level == .52f, "Profile upsert survives serialization");
    var before = demo.Read(playback.Id);
    Reject(() => ProfileStore.Apply(demo, playback, profile), "Cross-device/direction profile rejected before writes");
    Check(demo.Read(playback.Id) == before, "Rejected profile leaves playback untouched");
    Reject(() => ProfileStore.Apply(demo, mic with { Usb = mic.Usb! with { InstanceId = "SECOND-B20" } }, profile), "Same model with different USB instance is isolated");
    var state = ProfileStore.Apply(demo, mic with { Id = mic.Id }, profile);
    Check(state.Level == .47f && state.Muted, "Matching profile applies level and mute");
    Reject(() => store.Save(profile with { Level = float.PositiveInfinity }), "Invalid level is rejected without replacing the profile file");
    Check(store.Load()[0].Level == .52f, "Invalid save preserves existing profile");
    File.WriteAllText(Path.Combine(scratch, "profiles.json"), "[{\"Name\":\"Bad\",\"EndpointId\":\"x\",\"DeviceIdentity\":\"x\",\"Direction\":0,\"Level\":8,\"Muted\":false}]");
    Reject(() => store.Load(), "Corrupt profile values cannot reach audio controls");
} finally { foreach (var file in Directory.GetFiles(scratch)) File.Delete(file); Directory.Delete(scratch); }
var captured = (JsonArray)JsonNode.Parse("""
    [{"requestType":"13","SelectedDevice":[{"VID":"5013","PID":"152","SerialNumber":"DEMO","DeviceType":"WiredUSB","apoParams":{"directSoundEnabled":false,"reverbLevel":0.2,"speakerEqLevels":[1,0,0,-1,2]}}]}]
    """)!;
var original = captured.ToJsonString();
var prepared = SuiteApoRequest.PrepareSurround(captured, playback.Usb!, true);
var update = (JsonArray)JsonNode.Parse(Encoding.ASCII.GetString(prepared.ApplyMessage))!;
Check((bool)update[0]!["SelectedDevice"]![0]!["apoParams"]!["directSoundEnabled"]!, "Surround builder uses recovered true = 7.1 semantics");
Check(update[0]!["SelectedDevice"]![0]!["apoParams"]!["speakerEqLevels"]!.ToJsonString() == "[1,0,0,-1,2]", "Surround update preserves current EQ settings");
Check(captured.ToJsonString() == original && Encoding.ASCII.GetString(prepared.RestoreMessage) == original, "Surround builder leaves snapshot intact and prepares restoration");
Reject(() => SuiteApoRequest.PrepareSurround(captured, mic.Usb!, true), "Surround request rejects a foreign device snapshot");
var incomplete = (JsonArray)captured.DeepClone(); incomplete[0]!["SelectedDevice"]![0]!["apoParams"]!["directSoundEnabled"] = null;
Reject(() => SuiteApoRequest.PrepareSurround(incomplete, playback.Usb!, true), "Surround request rejects an unknown starting state");


} }
