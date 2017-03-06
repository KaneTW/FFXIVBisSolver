using System;
using System.Linq;
using SaintCoinach.Xiv;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.Utilities;

namespace FFXIVBisSolverCLI
{
    public class ClassJobConverter : XivConverter
    {
        public ClassJobConverter(XivCollection coll) : base(coll)
        {
        }

        public override bool Accepts(Type type)
        {
            return type == typeof (ClassJob);
        }

        public override object ReadYaml(IParser parser, Type type)
        {
            var value = parser.Expect<Scalar>().Value;
            return
                XivCollection.GetSheet<ClassJob>()
                    .Single(e => string.Equals(e.Abbreviation, value, StringComparison.InvariantCultureIgnoreCase));
        }

        public override void WriteYaml(IEmitter emitter, object value, Type type)
        {
            var job = (ClassJob) value;
            emitter.Emit(new Scalar(job.Abbreviation));
        }
    }

    public class BaseParamConverter : XivConverter
    {
        public BaseParamConverter(XivCollection coll) : base(coll)
        {
        }

        public override bool Accepts(Type type)
        {
            return type == typeof (BaseParam);
        }

        public override object ReadYaml(IParser parser, Type type)
        {
            var value = parser.Expect<Scalar>().Value;
            return XivCollection.GetSheet<BaseParam>().Single(bp => string.Equals(bp.Name, value, StringComparison.InvariantCultureIgnoreCase));
        }

        public override void WriteYaml(IEmitter emitter, object value, Type type)
        {
            var bp = (BaseParam) value;
            emitter.Emit(new Scalar(bp.Name));
        }
    }

    public class EquipSlotConverter : XivConverter
    {
        public EquipSlotConverter(XivCollection coll) : base(coll)
        {
        }

        public override bool Accepts(Type type)
        {
            return type == typeof(EquipSlot);
        }

        public override object ReadYaml(IParser parser, Type type)
        {
            var value = parser.Expect<Scalar>().Value;
            return XivCollection.EquipSlots.Single(s => string.Equals(s.Name, value, StringComparison.InvariantCultureIgnoreCase));
        }

        public override void WriteYaml(IEmitter emitter, object value, Type type)
        {
            var s = (EquipSlot)value;
            emitter.Emit(new Scalar(s.Name));
        }
    }

    public class ItemConverter : XivConverter
    {
        public ItemConverter(XivCollection coll) : base(coll)
        {
        }

        public override bool Accepts(Type type)
        {
            return type.IsSubclassOf(typeof (Item));
        }

        public override object ReadYaml(IParser parser, Type type)
        {
            var value = TypeConverter.ChangeType<int>(parser.Expect<Scalar>().Value);
            return XivCollection.GetSheet<Item>()[value];
        }

        public override void WriteYaml(IEmitter emitter, object value, Type type)
        {
            var item = (Item) value;
            emitter.Emit(new Scalar(item.Key.ToString()));
        }
    }

    public abstract class XivConverter : IYamlTypeConverter
    {
        protected XivConverter(XivCollection coll)
        {
            XivCollection = coll;
        }

        public XivCollection XivCollection { get; }
        public abstract bool Accepts(Type type);
        public abstract object ReadYaml(IParser parser, Type type);
        public abstract void WriteYaml(IEmitter emitter, object value, Type type);
    }
}