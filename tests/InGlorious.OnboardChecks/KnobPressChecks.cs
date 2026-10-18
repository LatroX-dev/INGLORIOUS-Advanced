using InGlorious.Core;

internal static class KnobPressChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        void Reject(Action action, string label)
        { try { action(); } catch (InvalidDataException) { check(true, label); return; } throw new Exception(label); }
        foreach (var board in BoardDescriptorRegistry.ProtocolBoards)
        {
            var project = ProjectService.Create(board.DeviceId);
            var layer = project.Layers[0];layer.KnobPressByFunction = true;
            if (!board.HasEncoder)
            { Reject(() => OnboardSerializer.Prepare(project), "Encoderless board rejects function-specific press");continue; }
            check(KnobPresets.GetPressBinding(layer,1)=="KC_TRNS" && layer.KnobPressBindings.Count==0,"Default per-function state inherits without changing the general key");
            var initial = OnboardSerializer.Prepare(ProjectService.Create(board.DeviceId));
            var image = OnboardSerializer.Prepare(project);
            check(image.SchemaVersion==7 && image.Payload.Length==OnboardSerializer.Shape(board.DeviceId,6).Size+64 && image.Payload[17]==16,
                "Enabled empty per-function map has canonical schema7 and exact trailer");
            check(OnboardSerializer.Prepare(OnboardSerializer.Decode(image.Payload)).Payload.SequenceEqual(image.Payload),"Empty enabled per-function mode round-trips exactly");
            string[] bindings=["KC_MUTE","KC_NO","KC_MPLY","KC_TRNS","IG_ZOOM_IN_MAC","MS_WHLL","IG_TAB_NEXT","IG_UNDO"];
            for(var i=0;i<8;i++)KnobPresets.SetPressBinding(layer,1<<i,bindings[i]);
            layer.KnobCycleOrder=KnobPresets.DecodeOrder(19873);layer.KnobCycleEnabled=true;
            image=OnboardSerializer.Prepare(project);
            for(var i=0;i<8;i++)check(OnboardSerializer.Get16(image.Payload,OnboardSerializer.Shape(board.DeviceId,6).Size+i*2)==OnboardSerializer.EncodeBinding(bindings[i],[]),"Function-specific code has fixed slot and inherit sentinel");
            check(OnboardSerializer.Prepare(OnboardSerializer.Decode(image.Payload)).Payload.SequenceEqual(image.Payload),"All press slots preserve order colors and bindings across wire round-trip");
            layer.KnobPressByFunction=false;
            var disabled=OnboardSerializer.Prepare(project);
            check(disabled.SchemaVersion==7 && disabled.Payload[17]==1 && OnboardSerializer.Decode(disabled.Payload).Layers[0].KnobPressBindings.Count==7,"Disabling the feature preserves explicit mappings");
            foreach(var unsafeBinding in new[]{"KC_LCTL","KC_RGUI","MO(1)","TG(2)","TO(0)","LCTL(KC_C)","unknown"})
                Reject(()=>KnobPresets.SetPressBinding(layer,1,unsafeBinding),"Unsupported held action cannot become a short tap");
            Reject(()=>KnobPresets.SetPressBinding(layer,3,"KC_A"),"Only real function bits accept mappings");
            foreach(var value in new[]{"KC_A","KC_MUTE","MS_WHLU","RM_VALU","IG_ZOOM_IN_MAC","KC_NO","KC_TRNS","MACRO:example"})
                check(KnobPresets.IsPressOverrideAssignable(value),"Assignable short-tap action is available: "+value);
            KnobPresets.SetPressBinding(layer,1,"MACRO:missing");
            Reject(()=>OnboardSerializer.Prepare(project),"Unknown macro reference is rejected before serialization");
            project.Macros.Add(new(){Id="example",Name="Example",Steps=[new(){Kind="down",Key="KC_A"},new(){Kind="up",Key="KC_A"}]});
            KnobPresets.SetPressBinding(layer,1,"MACRO:example");
            var macro=OnboardSerializer.Prepare(project);
            check(OnboardSerializer.Prepare(OnboardSerializer.Decode(macro.Payload)).Payload.SequenceEqual(macro.Payload),"Macro press slots preserve canonical descriptor references");
            var bad=macro.Payload.ToArray();OnboardSerializer.Put16(bad,OnboardSerializer.Shape(board.DeviceId,6).Size,0xE0);
            Reject(()=>OnboardSerializer.Decode(bad),"Incoming modifier override is rejected");
            bad=macro.Payload.ToArray();bad[31]=0x10;
            Reject(()=>OnboardSerializer.Decode(bad),"Per-function flags do not authorize Mac cycle flags");
            var allDefault=ProjectService.Create(board.DeviceId);allDefault.Layers[0].KnobPressByFunction=true;
            bad=OnboardSerializer.Prepare(allDefault).Payload;bad[17]=0;
            Reject(()=>OnboardSerializer.Decode(bad),"Redundant schema7 is rejected instead of silently changing schema");
            foreach(var (patch,flags,accepted) in new[]{(7,123,false),(8,59,false),(8,123,true)})
            {
                var target=new EmulatedDevice(board.DeviceId,initial.Payload){FirmwareVersion=new(0,11,(ushort)patch),ExtensionFlags=(ushort)flags,EffectsSupported=true,TimingSupported=true,ExtraFlags=224};
                await using var client=new OnboardClient(target,board.DeviceId);
                if(accepted)
                {check((await client.ApplyAsync(macro)).ReadbackVerified && target.Active.SequenceEqual(macro.Payload),"Schema7 transfers only with current verified firmware and exact capability");}
                else
                {
                    try{await client.ApplyAsync(macro);throw new Exception("Unsupported press map transferred");}
                    catch(InvalidDataException){check(!target.Commands.Any(command=>command is 3 or 4 or 5),"Old firmware or missing feature rejects before any write");}
                }
            }
        }
    }
}
