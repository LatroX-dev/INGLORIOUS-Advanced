using System.Buffers.Binary;
using System.Text.Json;
using InGlorious.Core;

var checks=0;
void Check(bool value,string why) { checks++;if(!value)throw new Exception(why); }
void Reject(Action action,string why) { try {action();}catch(InvalidDataException){checks++;return;}throw new Exception(why); }
async Task RejectAsync(Func<Task> action,string why) { try {await action();}catch(Exception e)when(e is InvalidDataException or IOException){checks++;return;}throw new Exception(why); }
foreach(var id in new[]{"pro-iso","numpad"}) {
 var device=DeviceCatalog.Get(id);var project=ProjectService.Create(id);var legacy=OnboardSerializer.Prepare(project);
 project.Layers[0].Keys["encoder:ccw"].Binding="KC_MAIL";project.Layers[0].Keys["encoder:cw"].Binding="KC_MYCM";
 project.Layers[0].LightingEffect=new(){Mode=2,Speed=255,Colors=["#FF0000","#0000FF"]};
 project.Layers[0].LedEffects[0]=new(){Mode=2,Colors=["#00ff00"]};
 project.Layers[3].LightingEffect=new(){Mode=0,Colors=["#ff0000","#0000ff"]};
 var image=OnboardSerializer.Prepare(project);var offset=OnboardSerializer.SequenceOffset(id);var size=OnboardSerializer.Shape(id).Size;
 var shape=OnboardSerializer.Shape(id);var encoderOffset=32+4*shape.Rows*shape.Cols*2;
 Check(OnboardSerializer.Get16(image.Payload,encoderOffset)==0xB1&&OnboardSerializer.Get16(image.Payload,encoderOffset+2)==0xB3,"consumer keycodes match pinned QMK keycodes.h");
 Check(image.SchemaVersion==3&&image.RequiresColorSequences&&image.MacroCapacity==(id=="pro-iso"?668:702)&&image.Payload.Length==OnboardSerializer.Shape(id,2).Size,"V3 uses old RAM size and honest macro limit");
 Check(image.Payload[offset]==2&&image.Payload[offset+25]==1&&image.Payload[offset+50]==0,"deduplicate exact ordered sequences case-insensitively");
 Check(image.Payload[size+5]==1&&image.Payload[offset+200]==2&&image.Payload[size+24+5]==1,"global and LED packed references use first-occurrence ordering");
 var decoded=OnboardSerializer.Decode(image.Payload);
 Check(OnboardSerializer.Prepare(decoded).Payload.SequenceEqual(image.Payload),"V3 byte-exact roundtrip with non-active stored sequence");
 var clone=ProjectService.Clone(project);clone.Layers[0].LightingEffect.Colors[0]="#ABCDEF";
 Check(project.Layers[0].LightingEffect.Colors[0]=="#FF0000"&&decoded.Layers[0].LedEffects[0].Colors.SequenceEqual(new[]{"#00FF00"}),"clone and import do not share mutable sequence lists");
 var serialized=JsonSerializer.Serialize(project);Check(JsonSerializer.Deserialize<AppProject>(serialized)!.Layers[0].LightingEffect.Colors.Count==2,"JSON retains sequence order");
 var bad=ProjectService.Clone(project);bad.Layers[0].LightingEffect.Colors=null!;Reject(()=>OnboardSerializer.Prepare(bad),"null palette rejected");
 bad=ProjectService.Clone(project);bad.Layers[0].LightingEffect.Colors=["#12345G"];Reject(()=>OnboardSerializer.Prepare(bad),"invalid RGB rejected");
 bad=ProjectService.Clone(project);bad.Layers[0].LightingEffect.Colors=Enumerable.Repeat("#112233",9).ToList();Reject(()=>OnboardSerializer.Prepare(bad),"nine colors never truncated");
 var eight=ProjectService.Create(id);
 for(var i=0;i<8;i++)eight.Layers[0].LedEffects[i]=new(){Mode=2,Colors=Enumerable.Range(0,8).Select(n=>$"#{i+1:X2}{n:X2}00").ToList()};
 var full=OnboardSerializer.Prepare(eight);Check(OnboardSerializer.Prepare(OnboardSerializer.Decode(full.Payload)).Payload.SequenceEqual(full.Payload),"all eight full sequences roundtrip");
 eight.Layers[1].LightingEffect.Colors=["#FA1234"];Reject(()=>OnboardSerializer.Prepare(eight),"ninth unique sequence rejected");
 foreach(var (at,value) in new[]{(offset,9),(offset+7,1),(offset+50,1),(offset+200,15),(offset+201,1),(size+5,9),(size-1,1)}) {
  var bytes=image.Payload.ToArray();bytes[at]=(byte)value;Reject(()=>OnboardSerializer.Decode(bytes),"bad count/tail/pool/reference/inheritance/padding rejected");
 }
 var gap=image.Payload.ToArray();gap[size+5]=2;Reject(()=>OnboardSerializer.Decode(gap),"noncanonical first use rejected");
 var cap=image.Payload.ToArray();OnboardSerializer.Put16(cap,12,768);Reject(()=>OnboardSerializer.Decode(cap),"V3 cannot claim legacy macro capacity");
 var excessive=image.Payload.ToArray();OnboardSerializer.Put16(excessive,14,image.MacroCapacity+1);Reject(()=>OnboardSerializer.Decode(excessive),"macro events cannot overlap sequence storage");
 var limits=ProjectService.Clone(project);
 for(var at=0;at<image.MacroCapacity;) {var count=Math.Min(128,image.MacroCapacity-at);limits.Macros.Add(new(){Id="m"+at,Name="Macro",Steps=Enumerable.Range(0,count).Select(n=>new MacroStep{Kind=n%2==0?"down":"up",Key="KC_A"}).ToList()});at+=count;}
 Check(OnboardSerializer.Prepare(limits).MacroEvents==image.MacroCapacity,"exact reduced event capacity remains usable");
 limits.Macros.Add(new(){Id="extra",Name="Extra",Steps=[new(){Kind="down",Key="KC_A"},new(){Kind="up",Key="KC_A"}]});Reject(()=>OnboardSerializer.Prepare(limits),"overflow reports error without shortening macros");
 var old=new Device(id,legacy.Payload,25);await using(var client=new OnboardClient(old,id)){await RejectAsync(()=>client.ApplyAsync(image),"V3 rejected by firmware without sequence capability");Check(old.Commands.SequenceEqual(new byte[]{1}),"old firmware receives INFO only, no BEGIN");}
 var emulated=new Device(id,legacy.Payload,121);await using(var client=new OnboardClient(emulated,id)) {
  var saved=await client.ApplyAsync(image);Check(saved.ReadbackVerified&&emulated.Active.SequenceEqual(image.Payload),"V3 apply completes exactly one commit and full readback");
  Check((await client.GetInfoAsync()).SupportsColorSequences,"sequence capability exposed");
  var before=emulated.Active.ToArray();var gen=emulated.Generation;emulated.Commands.Clear();
  foreach(byte value in new byte[]{0,1,127,254,255}){Check(await client.SetRuntimeBrightnessAsync(value)==value,"runtime set ACK exact");Check((await client.GetRuntimeBrightnessAsync()).Brightness==value,"runtime read exact");}
  Check(emulated.Generation==gen&&emulated.Active.SequenceEqual(before)&&emulated.Commands.All(c=>c is 1 or 9 or 10),"runtime dimmer never enters persistent config API");
  emulated.BadRuntimeAck=true;await RejectAsync(()=>client.SetRuntimeBrightnessAsync(23),"malformed runtime ACK rejected without retry");emulated.BadRuntimeAck=false;
  emulated.Staging=1;emulated.Commands.Clear();await RejectAsync(()=>client.SetRuntimeBrightnessAsync(23),"staging prohibits runtime set");Check(emulated.Commands.SequenceEqual(new byte[]{1}),"blocked setter sends INFO only");emulated.Staging=0;
  await client.ApplyAsync(legacy);Check(emulated.Active.SequenceEqual(legacy.Payload),"explicit legacy restore still byte-exact");
 }
 await using(var client=new OnboardClient(old,id)){old.Commands.Clear();await RejectAsync(()=>client.GetRuntimeBrightnessAsync(),"runtime read unavailable on old firmware");await RejectAsync(()=>client.SetRuntimeBrightnessAsync(1),"runtime set unavailable on old firmware");Check(old.Commands.All(c=>c==1),"no commands9/10 to old firmware");}
 var scene=LightingPreviewEngine.CreateScene(device,project.Layers[0]);var frame=new PreviewRgb[device.LedCount];scene.Render(0,[],frame);Check(frame[0]==new PreviewRgb(0,255,0)&&frame[1]==new PreviewRgb(255,0,0),"local preview separates global and individual sequences");
 scene.Render(100,[],frame);Check(frame[1]==new PreviewRgb(127,0,127),"preview crossfade matches runtime midpoint");
 project.Layers[0].LightingEffect.Mode=0;scene=LightingPreviewEngine.CreateScene(device,project.Layers[0]);scene.Render(100,[],frame);Check(frame[1]==PreviewRgb.FromHex(project.Layers[0].Keys[device.Keys.First(k=>k.LedIndex==1).Id].Color),"inactive sequence retained but not rendered in static mode");
 if(args.Length>0){Directory.CreateDirectory(args[0]);File.WriteAllBytes(Path.Combine(args[0],id+"-sequences.bin"),image.Payload);}
}
if(args.Length>1) {
 using var input=new BinaryReader(File.OpenRead(args[1]));var colors=new PreviewRgb[8];var count=0;
 while(input.BaseStream.Position<input.BaseStream.Length){var size=input.ReadByte();var phase=input.ReadByte();for(var i=0;i<8;i++)colors[i]=new(input.ReadByte(),input.ReadByte(),input.ReadByte());var expected=new PreviewRgb(input.ReadByte(),input.ReadByte(),input.ReadByte());Check(LightingPreviewEngine.Sequence(phase,colors.AsSpan(0,size))==expected,"actual C palette oracle pixel");count++;}
 Check(count==131072,"complete eight-color/count/phase oracle corpus");
}
Console.WriteLine($"PASS {checks} color-sequence and volatile-brightness checks; emulated transport, no hardware.");

sealed class Device(string id,byte[] initial,byte flags):IOnboardTransport {
 public byte[] Active=initial.ToArray();public uint Generation;public byte Brightness=255;public int Staging;public bool BadRuntimeAck;private byte[]? stage;public List<byte> Commands=[];
 public Task<byte[]> ExchangeAsync(byte[] p,CancellationToken ct){ct.ThrowIfCancellationRequested();var command=p[3];Commands.Add(command);var body=p.AsSpan(8,p[6]);byte[] reply;
  switch(command){
   case 1:var s=OnboardSerializer.Shape(id);var schema=BinaryPrimitives.ReadUInt16LittleEndian(Active.AsSpan(4));reply=new byte[24];reply[0]=s.Board;reply[1]=(byte)s.Rows;reply[2]=(byte)s.Cols;reply[3]=4;reply[4]=(byte)s.Leds;reply[5]=32;reply[6]=128;reply[7]=(byte)(flags|(Staging>0?4:0)|(BoardDescriptorRegistry.Get(id).HasSlider?2:0));U16(reply,8,Active.Length);U16(reply,10,OnboardSerializer.MacroCapacity(id,schema));U32(reply,12,Generation);U32(reply,16,OnboardProtocol.Crc32(Active));U16(reply,20,Staging);U16(reply,22,schema);break;
   case 2:var at=BinaryPrimitives.ReadUInt16LittleEndian(body);reply=new byte[4+body[2]];U16(reply,0,at);reply[2]=body[2];Active.AsSpan(at,body[2]).CopyTo(reply.AsSpan(4));break;
   case 4:stage=new byte[BinaryPrimitives.ReadUInt16LittleEndian(body)];reply=new byte[2];break;
   case 3:at=BinaryPrimitives.ReadUInt16LittleEndian(body);body[4..].CopyTo(stage!.AsSpan(at));reply=new byte[2];U16(reply,0,at+body[2]);break;
   case 5:Active=stage!;stage=null;Generation++;reply=new byte[8];U32(reply,0,Generation);U32(reply,4,OnboardProtocol.Crc32(Active));break;
   case 6:stage=null;reply=[];break;
   case 9:reply=[Brightness,Active[16],0,0];break;
   case 10:Brightness=body[0];reply=[(byte)(BadRuntimeAck?Brightness^1:Brightness)];break;
   default:throw new Exception("Unexpected emulated command");
  }
  var packet=OnboardProtocol.Packet(1,p[4],reply);packet[3]=command;return Task.FromResult(packet);
 }
 static void U16(byte[]b,int o,int v)=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o),(ushort)v);static void U32(byte[]b,int o,uint v)=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o),v);public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}
