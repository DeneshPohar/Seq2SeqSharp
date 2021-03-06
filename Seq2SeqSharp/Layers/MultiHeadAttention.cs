﻿using Seq2SeqSharp.Tools;
using System;
using System.Collections.Generic;
using System.IO;

namespace Seq2SeqSharp
{
    internal class MultiHeadAttention
    {
        private readonly IWeightTensor W0;
        private readonly IWeightTensor b0;

        private readonly IWeightTensor Q;
        private readonly IWeightTensor K;
        private readonly IWeightTensor V;

        private readonly IWeightTensor Qb;
        private readonly IWeightTensor Kb;
        private readonly IWeightTensor Vb;

        private readonly LayerNormalization layerNorm1;

        private readonly int m_hiddenDim;
        private readonly int m_d;
        private readonly int m_multiHeadNum;
        private readonly string m_name;
        private readonly float m_dropoutRatio;

        public MultiHeadAttention(string name, int multiHeadNum, int hiddenDim, int inputDim, float dropoutRatio, int deviceId, bool isTrainable)
        {
            m_name = name;
            m_hiddenDim = hiddenDim;
            m_multiHeadNum = multiHeadNum;
            m_d = m_hiddenDim / m_multiHeadNum;
            m_dropoutRatio = dropoutRatio;

            W0 = new WeightTensor(new long[2] { hiddenDim, hiddenDim }, deviceId, name: $"{name}.{nameof(W0)}", isTrainable: isTrainable, normal: NormType.Uniform);
            b0 = new WeightTensor(new long[2] { 1, hiddenDim }, 0, deviceId, name: $"{name}.{nameof(b0)}", isTrainable: isTrainable);

            Q = new WeightTensor(new long[2] { inputDim, hiddenDim }, deviceId, name: $"{name}.{nameof(Q)}", isTrainable: isTrainable);
            Qb = new WeightTensor(new long[2] { 1, hiddenDim }, 0, deviceId, name: $"{name}.{nameof(Qb)}", isTrainable: isTrainable);

            K = new WeightTensor(new long[2] { inputDim, hiddenDim }, deviceId, name: $"{name}.{nameof(K)}", isTrainable: isTrainable);
            Kb = new WeightTensor(new long[2] { 1, hiddenDim }, 0, deviceId, name: $"{name}.{nameof(Kb)}", isTrainable: isTrainable);

            V = new WeightTensor(new long[2] { inputDim, hiddenDim }, deviceId, name: $"{name}.{nameof(V)}", isTrainable: isTrainable);
            Vb = new WeightTensor(new long[2] { 1, hiddenDim }, 0, deviceId, name: $"{name}.{nameof(Vb)}", isTrainable: isTrainable);


            layerNorm1 = new LayerNormalization($"{name}.{nameof(layerNorm1)}", hiddenDim, deviceId, isTrainable);
        }

        /// <summary>
        /// Scaled multi-heads attention component with skip connectioned feed forward layers
        /// </summary>
        /// <param name="inputQ">The input Q tensor</param>
        /// <param name="inputK">The input K tensor</param>
        /// <param name="inputV">The input V tensor</param>
        /// <param name="batchSize">Batch size of input data set</param>
        /// <param name="graph">The instance of computing graph</param>
        /// <returns>Transformered output tensor</returns>
        public IWeightTensor Perform(IWeightTensor inputQ, IWeightTensor inputK, IWeightTensor inputV, IWeightTensor keyMask, int batchSize, IComputeGraph graph)
        {
            using (IComputeGraph g = graph.CreateSubGraph($"{m_name}_MultiHeadAttention"))
            {
                int seqLenQ = inputQ.Rows / batchSize;

                // SeqLenK must be euqal to SeqLenV
                int seqLenK = inputK.Rows / batchSize;
                int seqLenV = inputV.Rows / batchSize;

                IWeightTensor inputQNorm = layerNorm1.Norm(inputQ, g);
                IWeightTensor inputKNorm = (inputK == inputQ) ? inputQNorm : inputK; // layerNorm1.Norm(inputK, g);
                IWeightTensor inputVNorm = (inputK == inputV) ? inputKNorm : inputV; // layerNorm1.Norm(inputV, g);

                //Input projections
                IWeightTensor allQ = g.View(g.Affine(inputQNorm, Q, Qb), dims: new long[] { batchSize, seqLenQ, m_multiHeadNum, m_d });
                IWeightTensor allK = g.View(g.Affine(inputKNorm, K, Kb), dims: new long[] { batchSize, seqLenK, m_multiHeadNum, m_d });
                IWeightTensor allV = g.View(g.Affine(inputVNorm, V, Vb), dims: new long[] { batchSize, seqLenV, m_multiHeadNum, m_d });

                //Multi-head attentions
                IWeightTensor Qs = g.View(g.Permute(allQ, 2, 0, 1, 3), dims: new long[] { m_multiHeadNum * batchSize, seqLenQ, m_d });
                IWeightTensor Ks = g.View(g.Permute(allK, 2, 0, 3, 1), dims: new long[] { m_multiHeadNum * batchSize, m_d, seqLenK });
                IWeightTensor Vs = g.View(g.Permute(allV, 2, 0, 1, 3), dims: new long[] { m_multiHeadNum * batchSize, seqLenV, m_d });

                // Scaled softmax
                float scale = 1.0f / (float)Math.Sqrt(m_d);
                IWeightTensor attn = g.MulBatch(Qs, Ks, m_multiHeadNum * batchSize, scale);
                IWeightTensor attn2 = g.View(attn, dims: new long[] { m_multiHeadNum * batchSize * seqLenQ, seqLenK });


                if (keyMask != null)
                {
                    // attn2 = g.Add(attn2, mask, runGradient2: false);
                    attn2 = g.MaskFill(attn2, keyMask, -1e9f);
                }

                IWeightTensor softmax = g.Softmax(attn2, inPlace: true);
                IWeightTensor softmax2 = g.View(softmax, dims: new long[] { m_multiHeadNum * batchSize, seqLenQ, seqLenK });
                IWeightTensor o = g.View(g.MulBatch(softmax2, Vs, m_multiHeadNum * batchSize), dims: new long[] { m_multiHeadNum, batchSize, seqLenQ, m_d });
                IWeightTensor W = g.View(g.Permute(o, 1, 2, 0, 3), dims: new long[] { batchSize * seqLenQ, m_multiHeadNum * m_d });

                // Output projection
                IWeightTensor finalAttResults = g.Dropout(g.Affine(W, W0, b0), batchSize, m_dropoutRatio, inPlace: true);

                return graph.Add(finalAttResults, inputQ);
            }
        }


        public virtual List<IWeightTensor> getParams()
        {
            List<IWeightTensor> response = new List<IWeightTensor>
            {
                Q,
                Qb,

                K,
                Kb,

                V,
                Vb,

                W0,
                b0
            };

            response.AddRange(layerNorm1.getParams());

            return response;
        }


        public void Save(Stream stream)
        {
            Q.Save(stream);
            Qb.Save(stream);

            K.Save(stream);
            Kb.Save(stream);

            V.Save(stream);
            Vb.Save(stream);

            W0.Save(stream);
            b0.Save(stream);

            layerNorm1.Save(stream);
        }


        public void Load(Stream stream)
        {
            Q.Load(stream);
            Qb.Load(stream);

            K.Load(stream);
            Kb.Load(stream);

            V.Load(stream);
            Vb.Load(stream);

            W0.Load(stream);
            b0.Load(stream);

            layerNorm1.Load(stream);
        }
    }
}
