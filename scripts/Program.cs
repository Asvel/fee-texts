using System.Text;
using System.Xml;
using AssetsTools.NET.Extra;
using YamlDotNet.Core;
using YamlDotNet.Serialization.EventEmitters;
using YamlDotNet.Serialization;

var dataPath = args[0];
var langs = new string[] { "jpja", "cnch", "usen" };

var personIdToTextId = new Dictionary<string, string>();  // eg. リュール => MPID_Lueur, マルス => MGID_Marth
foreach (var (file, keyField, valueField) in new[] { ("person", "Pid", "Name"), ("god", "Gid", "Mid") })
{
    var am = new AssetsManager();
    var bun = am.LoadBundleFile(Path.Combine(dataPath, $@"StreamingAssets\aa\Switch\fe_assets_gamedata\{file}.xml.bundle"));
    var inst = am.LoadAssetsFileFromBundle(bun, 0, true);
    var inf = inst.table.GetAssetsOfType((int)AssetClassID.TextAsset)[0];
    var xmlData = am.GetTypeInstance(inst, inf).GetBaseField()["m_Script"].GetValue().AsString();
    var xml = new XmlDocument();
    xml.LoadXml(xmlData[1..]);  // BOM
    foreach (XmlNode node in xml.DocumentElement!.SelectSingleNode("/Book/Sheet")!.SelectNodes("Data/Param")!)
    {
        personIdToTextId.TryAdd(node.Attributes![keyField]!.InnerText[4..], node.Attributes[valueField]!.InnerText);
    }
}

var texts = new Dictionary<string, Dictionary<string, List<string>>>();
foreach (var lang in langs)
{
    var textIdToText = new Dictionary<string, string>();
    var dir = Path.Combine(dataPath, @"StreamingAssets\aa\Switch\fe_assets_message" , lang[..2], lang);
    foreach (var path in Directory.GetFiles(dir))
    {
        var filename = Path.GetFileNameWithoutExtension(path)[..^6];
        if (filename == "habdinner") continue;  // jp only
        if (lang == langs[0]) texts.Add(filename, new());

        // bundle
        var am = new AssetsManager();
        var bun = am.LoadBundleFile(path);
        if (bun.file.bundleInf6.directoryCount != 1) throw new InvalidDataException();
        var inst = am.LoadAssetsFileFromBundle(bun, 0, true);
        if (inst.file.assetCount != 2) throw new InvalidDataException();

        // asset
        var inf = inst.table.GetAssetsOfType((int)AssetClassID.TextAsset)[0];
        var baseField = am.GetTypeInstance(inst, inf).GetBaseField();
        var msbtData = baseField["m_Script"].value.value.asString;

        // msbt, http://problemkaputt.de/gbatek-3ds-files-messages-msgstdbn.htm
        var stream = new MemoryStream(msbtData);
        var reader = new BinaryReader(stream, Encoding.Unicode);
        if (reader.ReadUInt64() != 0x6e4264745367734d/*MsgStdBn*/) throw new InvalidDataException();
        if (reader.ReadUInt16() != 0xFEFF) throw new InvalidDataException();
        stream.Position += 2;
        if (reader.ReadByte() != 1) throw new InvalidDataException();
        if (reader.ReadByte() != 3) throw new InvalidDataException();
        var chunkCount = reader.ReadUInt16();
        stream.Position += 16;

        string[]? lbls = null;
        string[]? txts = null;
        for (; chunkCount > 0; chunkCount--)
        {
            var type = reader.ReadUInt32();
            var size = reader.ReadUInt32();
            stream.Position += 8;
            if (type == 0x314c424c/*LBL1*/)
            {
                var hashCount = reader.ReadUInt32();
                var lblCount = 0;
                for (; hashCount > 0; hashCount--)
                {
                    lblCount += reader.ReadInt32();
                    stream.Position += 4;
                }
                lbls = new string[lblCount];
                for (; lblCount > 0; lblCount--)
                {
                    var length = reader.ReadByte();
                    var lbl = Encoding.ASCII.GetString(reader.ReadBytes(length));
                    var index = reader.ReadInt32();
                    lbls[index] = lbl;
                }
            }
            else if (type == 0x32545854/*TXT2*/)
            {
                var count = reader.ReadUInt32();
                stream.Position += 4 * count;
                txts = new string[count];
                for (int index = 0; index < count; index++)
                {
                    var txt = new StringBuilder();
                    while (true)
                    {
                        var c = reader.ReadChar();
                        if (c == '\0') break;
                        if (c == '\u000e')
                        {
                            var type1 = reader.ReadUInt16();
                            var type2 = reader.ReadUInt16();
                            var parameterSize = reader.ReadUInt16();
                            var parameters = reader.ReadBytes(parameterSize);
                            if (type1 == 3 && type2 == 2)  // speaker
                            {
                                if (BitConverter.ToUInt16(parameters) != parameters.Length - 2) throw new InvalidDataException();
                                txt.Append('\u000e');
                                txt.Append(personIdToTextId[Encoding.Unicode.GetString(parameters[2..])]);
                                txt.Append('\u000e');
                            }
                            if (type1 != 2 && type1 != 3 && type1 != 4 && type1 != 5 &&
                                type1 != 7 && type1 != 11 && !(type1 == 6 && type2 == 0))
                            {
                                txt.Append('{');
                                txt.Append(type1);
                                txt.Append(',');
                                txt.Append(type2);
                                if (parameterSize > 0)
                                {
                                    txt.Append(',');
                                    txt.Append(parameterSize);
                                }
                                txt.Append('}');
                            }
                        }
                        else
                        {
                            txt.Append(c);
                        }
                    }
                    txts[index] = txt.ToString();
                }
            }
            else
            {
                stream.Position += size;
            }
            while (stream.ReadByte() == 0xAB) ;
            stream.Position -= 1;
        }
        if (lbls == null || txts == null || lbls.Length != txts.Length) throw new InvalidDataException();

        // texts
        foreach (var (lbl, txt) in lbls.Zip(txts))
        {
            if (lang == langs[0]) texts[filename].Add(lbl, new());
            texts[filename][lbl].Add(txt.Trim());

            if (lbl.StartsWith("MPID_") || lbl.StartsWith("MGID_"))
            {
                textIdToText.Add(lbl, txt);
            }
        }
    }
    foreach (var file in texts.Values)
    {
        foreach (var txts in file.Values)
        {
            var txt = txts[^1];
            var index = txt.IndexOf('\u000e');
            if (index != -1)
            {
                var endIndex = txt.IndexOf('\u000e', index + 1);
                var textId = txt[(index + 1)..(endIndex)];
                txts[^1] = string.Concat(txt[..index], "<", textIdToText[textId], ">", txt[(endIndex + 1)..]);
            }
        }
    }
}

var YamlSerializer = new SerializerBuilder()
    .WithEventEmitter(nextEmitter => new MultilineScalarFlowStyleEmitter(nextEmitter))
    .Build();
File.WriteAllText(@"..\..\..\..\texts.yaml", YamlSerializer.Serialize(texts));

class MultilineScalarFlowStyleEmitter : ChainedEventEmitter
{
    public MultilineScalarFlowStyleEmitter(IEventEmitter nextEmitter) : base(nextEmitter) { }

    public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
    {
        if (typeof(string).IsAssignableFrom(eventInfo.Source.Type))
        {
            string? value = eventInfo.Source.Value as string;
            if (!string.IsNullOrEmpty(value))
            {
                bool isMultiLine = value.IndexOfAny(new char[] { '\r', '\n', '\x85', '\x2028', '\x2029' }) >= 0;
                if (isMultiLine)
                {
                    eventInfo = new ScalarEventInfo(eventInfo.Source)
                    {
                        Style = ScalarStyle.Literal
                    };
                }
            }
        }
        nextEmitter.Emit(eventInfo, emitter);
    }
}
