using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FFXIVBisSolver;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace FFXIVBisSolverCLI
{
    class PiecewiseLinearConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type)
        {
            return type == typeof (PiecewiseLinearFunction);
        }

        public object ReadYaml(IParser parser, Type type)
        {
            var segments = new List<Tuple<int, int>>();
            parser.Expect<SequenceStart>();
            while (parser.Allow<SequenceEnd>() == null)
            {
                //TODO: tuple converter
                parser.Expect<SequenceStart>();
                var v1 = parser.Expect<Scalar>().Value;
                var v2 = parser.Expect<Scalar>().Value;
                parser.Expect<SequenceEnd>();
                segments.Add(Tuple.Create(int.Parse(v1), int.Parse(v2)));
            }
            return new PiecewiseLinearFunction(segments);
        }

        public void WriteYaml(IEmitter emitter, object value, Type type)
        {
            var func = (PiecewiseLinearFunction) value;
            emitter.Emit(new SequenceStart(null, null, true, SequenceStyle.Block));
            foreach (var seg in func.Segments)
            {
                emitter.Emit(new SequenceStart(null, null, true, SequenceStyle.Flow));
                emitter.Emit(new Scalar(seg.Item1.ToString()));
                emitter.Emit(new Scalar(seg.Item2.ToString()));
                emitter.Emit(new SequenceEnd());
            }
            emitter.Emit(new SequenceEnd());
        }
    }
}
