using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace FFXIVBisSolverCLI
{
    class TupleConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type)
        {
            return type.IsGenericType && type.GetInterfaces().Any(f => f.Name == "ITuple");
        }

        public object ReadYaml(IParser parser, Type type)
        {
            var value = parser.Expect<YamlDotNet.Core.Events.SequenceStart>();
            throw new NotImplementedException();
        }

        public void WriteYaml(IEmitter emitter, object value, Type type)
        {
            throw new NotImplementedException();
        }
    }
}
