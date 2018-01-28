using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace PythiaMatrixBuilder
{
    public class UsageDataModel
    {
        [JsonProperty("Repo")]
        public string Repo;

        [JsonProperty("Solution")]
        public string Solution;

        [JsonProperty("Project")]
        public string Project;

        [JsonProperty("Document")]
        public string Document;

        [JsonProperty("References")]
        public Dictionary<string, Dictionary<string, MethodProperties>> References;

    }

    public class MethodProperties
    {
        //containing class for the invocation
        [JsonProperty("containingClass")]
        public List<string> containingClass;

        //containing method or property for the invocation
        [JsonProperty("containingFunction")]
        public List<string> containingFunction;

        [JsonProperty("isInIfConditional")]
        public List<string> isInIfConditional;

        [JsonProperty("spanStart")]
        public List<string> spanStart;

        //this is the kind for the method or property
        [JsonProperty("typePM")]
        public List<string> typePM;

        //this is the kind for invocation symbol
        [JsonProperty("kind")]
        public List<string> kind;

        [JsonProperty("isOverride")]
        public List<string> isOverride;

        [JsonProperty("isStatic")]
        public List<string> isStatic;

        [JsonProperty("isExtern")]
        public List<string> isExtern;

        [JsonProperty("isDefinition")]
        public List<string> isDefinition;

        [JsonProperty("isVirtual")]
        public List<string> isVirtual;

        [JsonProperty("isAbstract")]
        public List<string> isAbstract;

        [JsonProperty("isSealed")]
        public List<string> isSealed;


        public void Append(MethodProperties properties)
        {
            containingClass.AddRange(properties.containingClass);
            containingFunction.AddRange(properties.containingFunction);
            isAbstract.AddRange(properties.isAbstract);
            isExtern.AddRange(properties.isExtern);
            isDefinition.AddRange(properties.isDefinition);
            isOverride.AddRange(properties.isOverride);
            isSealed.AddRange(properties.isSealed);
            isStatic.AddRange(properties.isStatic);
            isVirtual.AddRange(properties.isVirtual);
            kind.AddRange(properties.kind);
            spanStart.AddRange(properties.spanStart);
            typePM.AddRange(properties.typePM);
            isInIfConditional.AddRange(properties.isInIfConditional);
        }

    }

}
