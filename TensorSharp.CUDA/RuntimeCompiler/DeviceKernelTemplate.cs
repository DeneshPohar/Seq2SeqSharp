﻿using System;
using System.Collections.Generic;
using System.Linq;

namespace TensorSharp.CUDA.RuntimeCompiler
{
    public class DeviceKernelTemplate
    {
        private readonly string templateCode;
        private readonly List<string> requiredHeaders;
        private readonly HashSet<string> requiredConfigArgs = new HashSet<string>();
        private readonly Dictionary<KernelConfig, byte[]> ptxCache = new Dictionary<KernelConfig, byte[]>();


        public DeviceKernelTemplate(string templateCode, params string[] requiredHeaders)
        {
            this.templateCode = templateCode;
            this.requiredHeaders = new List<string>(requiredHeaders);
        }

        public void AddConfigArgs(params string[] args)
        {
            foreach (string item in args)
            {
                requiredConfigArgs.Add(item);
            }
        }

        public void AddHeaders(params string[] headers)
        {
            requiredHeaders.AddRange(headers);
        }

        public byte[] PtxForConfig(CudaCompiler compiler, KernelConfig config)
        {
            if (ptxCache.TryGetValue(config, out byte[] cachedResult))
            {
                return cachedResult;
            }

            if (!requiredConfigArgs.All(config.ContainsKey))
            {
                string allRequired = string.Join(", ", requiredConfigArgs);
                throw new InvalidOperationException("All config arguments must be provided. Required: " + allRequired);
            }

            // Checking this ensures that there is only one config argument that can evaluate to the same code,
            // which ensures that the ptx cacheing does not generate unnecessary combinations. Also, a mismatch
            // occurring here probably indicates a bug somewhere else.
            if (!config.Keys.All(requiredConfigArgs.Contains))
            {
                string allRequired = string.Join(", ", requiredConfigArgs);
                throw new InvalidOperationException("Config provides some unnecessary arguments. Required: " + allRequired);
            }

            //return new DeviceKernelCode(config.ApplyToTemplate(templateCode), requiredHeaders.ToArray());
            string finalCode = config.ApplyToTemplate(templateCode);

            byte[] result = compiler.CompileToPtx(finalCode, requiredHeaders.ToArray());
            ptxCache.Add(config, result);
            return result;
        }
    }
}
