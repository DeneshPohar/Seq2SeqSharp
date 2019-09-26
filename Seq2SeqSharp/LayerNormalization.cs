﻿using Seq2SeqSharp.Tools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Seq2SeqSharp
{
    [Serializable]
    class LayerNormalization
    {
        public IWeightMatrix alpha { get; set; }

        public IWeightMatrix beta { get; set; }

        public LayerNormalization(int dim, ArchTypeEnums archType, int deviceId)
        {
            alpha = new WeightTensor(1, dim, 1, deviceId);
            beta = new WeightTensor(1, dim, 0, deviceId);
        }

        public IWeightMatrix Process(IWeightMatrix input, IComputeGraph innerGraph)
        {
            var alphas = innerGraph.RepeatRows(alpha, input.Rows);
            var betas = innerGraph.RepeatRows(beta, input.Rows);

            return innerGraph.LayerNorm(input, alphas, betas);
        }

        public virtual List<IWeightMatrix> getParams()
        {
            List<IWeightMatrix> response = new List<IWeightMatrix>();
            response.Add(alpha);
            response.Add(beta);

            return response;
        }

        public void Save(Stream stream)
        {
            alpha.Save(stream);
            beta.Save(stream);
        }


        public void Load(Stream stream)
        {
            alpha.Load(stream);
            beta.Load(stream);
        }
    }
}
