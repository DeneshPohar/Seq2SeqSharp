﻿using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using TensorSharp;

/// <summary>
/// Tensor based computing graph written by Zhongkai Fu.
/// The graph includes the following key features.
/// #1. Include several key operations for neural network.
/// #2. Support both CPU and GPU (CUDA)
/// #3. Support automatic differentiation and back propagation
/// #4. Support networks (operations) visualization
/// #5. and so on...
/// </summary>
namespace Seq2SeqSharp.Tools
{
    public class ConcurrentList<T>
    {
        private const int MaxSize = 1024000;
        private T[] array;
        private int count = 0;
        public int Count => count;

        public T this[int key]
        {
            get => array[key];
            set => array[key] = value;
        }

        public ConcurrentList()
        {
            array = new T[MaxSize];
        }

        public void Add(T item)
        {
            int n = System.Threading.Interlocked.Increment(ref count);
            array[n - 1] = item;
        }

        public void RemoveLastItem()
        {
            System.Threading.Interlocked.Decrement(ref count);
        }

        private readonly object locker = new object();
        public void Clear()
        {
            lock (locker)
            {
                count = 0;
                array = new T[MaxSize];
            }
        }
    }

    public class ComputeGraphTensor : IComputeGraph
    {
        private readonly WeightTensorFactory m_weightTensorFactory;
        private readonly ConcurrentList<Action> m_backprop;
        private readonly bool m_needsBackprop;
        private readonly bool m_visNeuralNetwork;
        private readonly int m_deviceId;
        private readonly bool m_isSubGraph;

        public ComputeGraphTensor(IWeightFactory weightFactory, int deviceId, bool needBack = true, bool visNetwork = false, ConcurrentList<Action> backprop = null, bool isSubGraph = false)
        {
            m_backprop = backprop != null ? backprop : new ConcurrentList<Action>();
            m_weightTensorFactory = weightFactory as WeightTensorFactory;
            m_needsBackprop = needBack;
            m_deviceId = deviceId;
            m_visNeuralNetwork = visNetwork;
            m_isSubGraph = isSubGraph;
        }

        public IWeightFactory GetWeightFactory()
        {
            return m_weightTensorFactory;
        }

        public IComputeGraph CreateSubGraph(string name)
        {
            ComputeGraphTensor subGraph = new ComputeGraphTensor(m_weightTensorFactory, m_deviceId, m_needsBackprop, m_visNeuralNetwork, m_backprop, isSubGraph: true);

            return subGraph;
        }

        public void Backward()
        {
            for (int i = m_backprop.Count - 1; i >= 0; i--)
            {
                m_backprop[i](); // tick!
            }

            m_backprop.Clear();
        }

        public void RunTopBackward()
        {
            if (m_needsBackprop)
            {
                m_backprop[m_backprop.Count - 1]();
                m_backprop.RemoveLastItem();
            }
        }

        public IWeightTensor BuildPositionMatrix(int row, int column)
        {
            WeightTensor res = m_weightTensorFactory.BuildPositionWeightTensor(row, column, m_deviceId, name: "PositionEmbeddings", isTrainable: true);

            return res;
        }

        public IWeightTensor Sigmoid(IWeightTensor w)
        {
            WeightTensor m = w as WeightTensor;
            WeightTensor res = m_weightTensorFactory.CreateWeightTensor(m.Sizes, m_deviceId, name: $"{GetHashString(w.Name)}.Sigmoid");
            VisualizeNodes(w, res);

            Ops.Sigmoid(res.TWeight, m.TWeight);

            if (m_needsBackprop)
            {
                Action backward = () =>
                {
                    m.AddSigmoidGradient(res);
                    res.Dispose();
                };
                m_backprop.Add(backward);
            }

            return res;
        }


        public IWeightTensor AddTanh(IWeightTensor w1, IWeightTensor w2)
        {
            WeightTensor m1 = w1 as WeightTensor;
            WeightTensor m2 = w2 as WeightTensor;
            WeightTensor res = m_weightTensorFactory.CreateWeightTensor(m1.Sizes, m_deviceId, name: $"{GetHashString(w1.Name, w2.Name)}.AddTanh");
            VisualizeNodes(new IWeightTensor[] { w1, w2 }, res);

            Ops.AddTanh(res.TWeight, m1.TWeight, m2.TWeight);
            if (m_needsBackprop)
            {
                Action backward = () =>
                {
                    m1.AddTanhGradient(res);
                    m2.AddTanhGradient(res);
                    res.Dispose();
                };
                m_backprop.Add(backward);
            }

            return res;

        }

        public IWeightTensor AddTanh(IWeightTensor w1, IWeightTensor w2, IWeightTensor w3)
        {
            WeightTensor m1 = w1 as WeightTensor;
            WeightTensor m2 = w2 as WeightTensor;
            WeightTensor m3 = w3 as WeightTensor;
            WeightTensor res = m_weightTensorFactory.CreateWeightTensor(m1.Sizes, m_deviceId, name: $"{GetHashString(w1.Name, w2.Name, w3.Name)}.AddTanh");
            VisualizeNodes(new IWeightTensor[] { w1, w2, w3 }, res);

            Ops.AddTanh3(res.TWeight, m1.TWeight, m2.TWeight, m3.TWeight);
            if (m_needsBackprop)
            {
                Action backward = () =>
                {
                    m1.AddTanhGradient(res);
                    m2.AddTanhGradient(res);
                    m3.AddTanhGradient(res);

                    res.Dispose();
                };
                m_backprop.Add(backward);
            }

            return res;

        }

        public IWeightTensor Mul(IWeightTensor w, float v)
        {
            WeightTensor m = w as WeightTensor;
            WeightTensor res = m_weightTensorFactory.CreateWeightTensor(m.Sizes, m_deviceId, name: $"{GetHashString(w.Name)}.MulV", graphToBind: this);
            VisualizeNodes(w, res);

            Ops.Mul(res.TWeight, m.TWeight, v);

            if (m_needsBackprop)
            {
                Action backward = () =>
                {
                    res.ReleaseWeight();

                    Ops.AddMulV(m.TGradient, m.TGradient, res.TGradient, v);
                    res.Dispose();
                };
                m_backprop.Add(backward);
            }

            return res;
        }


        /// <summary>
        /// Operation r = w1 + w2 * v
        /// </summary>
        /// <param name="w1"></param>
        /// <param name="w2"></param>
        /// <param name="v"></param>
        /// <returns></returns>
        public IWeightTensor AddMul(IWeightTensor w1, IWeightTensor w2, float v, bool runGradientW1 = true, bool runGradientW2 = true)
        {
            WeightTensor m1 = w1 as WeightTensor;
            WeightTensor m2 = w2 as WeightTensor;

            WeightTensor res = m_weightTensorFactory.CreateWeightTensor(m1.Sizes, m_deviceId, name: $"{GetHashString(w1.Name, w2.Name)}.AddMulV", graphToBind: this);
            VisualizeNodes(new IWeightTensor[] { w1, w2 }, res);

            Ops.AddMulV(res.TWeight, m1.TWeight, m2.TWeight, v);

            if (m_needsBackprop && (runGradientW1 || runGradientW2))
            {
                Action backward = () =>
                {
                    res.ReleaseWeight();

                    if (runGradientW1)
                    {
                        m1.CopyOrAddGradient(res);
                    }

                    if (runGradientW2)
                    {
                        Ops.AddMulV(m2.TGradient, m2.TGradient, res.TGradient, v);
                    }

                    res.Dispose();
                };
                m_backprop.Add(backward);
            }

            return res;
        }


        public void Bind(IWeightTensor w)
        {
            m_tensorsBindToCurrentGraph.Add(w);
        }

        public void Unbind(IWeightTensor w)
        {
            m_tensorsBindToCurrentGraph.Remove(w);

        }

        /// <summary>
        /// Result = w1 * w2 + w3 * w4
        /// </summary>
        /// <param name="w1"></param>
        /// <param name="w2"></param>
        /// <param name="w3"></param>
        /// <param name="w4"></param>
        /// <returns></returns>
        public IWeightTensor EltMulMulAdd(IWeightTensor w1, IWeightTensor w2, IWeightTensor w3, IWeightTensor w4)
        {
            WeightTensor m1 = w1 as WeightTensor;
            WeightTensor m2 = w2 as WeightTensor;
            WeightTensor m3 = w3 as WeightTensor;
            WeightTensor m4 = w4 as WeightTensor;

            WeightTensor res = m_weightTensorFactory.CreateWeightTensor(m1.Sizes, m_deviceId, name: $"{GetHashString(w1.Name, w2.Name, w3.Name, w4.Name)}.EltMulMulAdd", graphToBind: this);
            VisualizeNodes(new IWeightTensor[] { w1, w2, w3, w4 }, res);

            Ops.MulMulAdd(res.TWeight, m1.TWeight, m2.TWeight, m3.TWeight, m4.TWeight);
            if (m_needsBackprop)
            {
                Action backward = () =>
                {
                    res.ReleaseWeight();

                    m1.AddMulGradient(m2.TWeight, res.TGradient);
                    m2.AddMulGradient(m1.TWeight, res.TGradient);

                    m3.AddMulGradient(m4.TWeight, res.TGradient);
                    m4.AddMulGradient(m3.TWeight, res.TGradient);

                    res.Dispose();
                };
                m_backprop.Add(backward);

                // These tensors' weights will be used during back-propogation, so we unbind them from the computing graph
                m1.UnbindFromComputeGraph();
                m2.UnbindFromComputeGraph();
                m3.UnbindFromComputeGraph();
                m4.UnbindFromComputeGraph();
            }


            return res;
        }

        public IWeightTensor EltMul(IWeightTensor w1, IWeightTensor w2)
        {
            WeightTensor m1 = w1 as WeightTensor;
            WeightTensor m2 = w2 as WeightTensor;
            WeightTensor res = m_weightTensorFactory.CreateWeightTensor(m1.Sizes, m_deviceId, name: $"{GetHashString(w1.Name, w2.Name)}.EltMul", graphToBind: this);
            VisualizeNodes(new IWeightTensor[] { w1, w2 }, res);

            Ops.Mul(res.TWeight, m1.TWeight, m2.TWeight);
            if (m_needsBackprop)
            {
                Action backward = () =>
                {
                    res.ReleaseWeight();

                    m1.AddMulGradient(m2.TWeight, res.TGradient);
                    m2.AddMulGradient(m1.TWeight, res.TGradient);

                    res.Dispose();
                };
                m_backprop.Add(backward);

                m1.UnbindFromComputeGraph();
                m2.UnbindFromComputeGraph();
            }

            return res;
        }

        public IWeightTensor Add(IWeightTensor w1, IWeightTensor w2, bool runGradient1 = true, bool runGradient2 = true)
        {
            WeightTensor m1 = w1 as WeightTensor;
            WeightTensor m2 = w2 as WeightTensor;
            WeightTensor res = m_weightTensorFactory.CreateWeightTensor(m1.Sizes, m_deviceId, name: $"{GetHashString(w1.Name, w2.Name)}.Add", graphToBind: this);
            VisualizeNodes(new IWeightTensor[] { w1, w2 }, res);

            Ops.Add(res.TWeight, m1.TWeight, m2.TWeight);

            if (m_needsBackprop)
            {
                Action backward = () =>
                {
                    res.ReleaseWeight();

                    if (runGradient1)
                    {
                        m1.CopyOrAddGradient(res);
                    }

                    if (runGradient2)
                    {
                        m2.CopyOrAddGradient(res);
                    }

                    res.Dispose();
                };
                m_backprop.Add(backward);
            }

            return res;
        }

        public IWeightTensor Tanh(IWeightTensor w)
        {
            WeightTensor m = w as WeightTensor;
            WeightTensor res = m_weightTensorFactory.CreateWeightTensor(m.Sizes, m_deviceId, name: $"{GetHashString(w.Name)}.Tanh");
            VisualizeNodes(w, res);

            Ops.Tanh(res.TWeight, m.TWeight);
            if (m_needsBackprop)
            {
                Action backward = () =>
                {
                    m.AddTanhGradient(res);
                    res.Dispose();
                };
                m_backprop.Add(backward);
            }

            return res;
        }


        public IWeightTensor Relu(IWeightTensor w)
        {
            WeightTensor m = w as WeightTensor;
            WeightTensor res = m_weightTensorFactory.CreateWeightTensor(m.Sizes, m_deviceId, name: $"{GetHashString(w.Name)}.Relu", graphToBind: this);
            VisualizeNodes(w, res);

            Ops.Relu(res.TWeight, m.TWeight);
            if (m_needsBackprop)
            {
                Action backward = () =>
                {
                    res.ReleaseWeight();
                    Ops.AddReluD(m.TGradient, m.TGradient, m.TWeight, res.TGradient);
                    res.Dispose();
                };
                m_backprop.Add(backward);

                m.UnbindFromComputeGraph();
            }

            return res;
        }


        public IWeightTensor MulBatch(IWeightTensor m1, IWeightTensor m2, int batchSize, float alpha = 1.0f)
        {
            WeightTensor t1 = m1 as WeightTensor;
            WeightTensor t2 = m2 as WeightTensor;
            WeightTensor res = m_weightTensorFactory.CreateWeightTensor((int)(batchSize * t1.TWeight.Sizes[1]), (int)t2.TWeight.Sizes[2], m_deviceId, name: $"{GetHashString(m1.Name, m2.Name)}.MulBatch", graphToBind: this);
            VisualizeNodes(new IWeightTensor[] { m1, m2 }, res);

            Tensor t1W = t1.TWeight;
            Tensor t2W = t2.TWeight;
            using (Tensor rW = res.TWeight.View(batchSize, t1.TWeight.Sizes[1], t2.TWeight.Sizes[2]))
            {
                Ops.AddmmBatch(rW, 0.0f, rW, alpha, t1W, t2W);
            }

            if (m_needsBackprop)
            {
                Action backward = () =>
                {
                    res.ReleaseWeight();
                    using (Tensor rG = res.TGradient.View(batchSize, t1.TWeight.Sizes[1], t2.TWeight.Sizes[2]))
                    {
                        using (Tensor t1G = t1.TGradient.View(t1.TWeight.Sizes[0], t1.TWeight.Sizes[1], t1.TWeight.Sizes[2]))
                        {
                            using (Tensor tW2 = t2W.Transpose(1, 2))
                            {
                                Ops.AddmmBatch(t1G, 1.0f, t1G, 1.0f, rG, tW2);
                            }
                        }
                        using (Tensor t2G = t2.TGradient.View(t2.TWeight.Sizes[0], t2.TWeight.Sizes[1], t2.TWeight.Sizes[2]))
                        {
                            using (Tensor tW1 = t1W.Transpose(1, 2))
                            {
                                Ops.AddmmBatch(t2G, 1.0f, t2G, 1.0f, tW1, rG);
                            }
                        }
                    }

                    res.Dispose();

                };
                m_backprop.Add(backward);

                t1.UnbindFromComputeGraph();
                t2.UnbindFromComputeGraph();
            }

            return res;
        }

        public IWeightTensor Mul(IWeightTensor m1, IWeightTensor m2)
        {
            WeightTensor t1 = m1 as WeightTensor;
            WeightTensor t2 = m2 as WeightTensor;
            int n = t1.Rows;
            int d = t2.Columns;
            WeightTensor res;

            res = m_weightTensorFactory.CreateWeightTensor(n, d, m_deviceId, name: $"{GetHashString(m1.Name, m2.Name)}.Mul", graphToBind: this);
            VisualizeNodes(new IWeightTensor[] { m1, m2 }, res);

            Ops.Addmm(res.TWeight, 0.0f, res.TWeight, 1.0f, t1.TWeight, t2.TWeight);
            if (m_needsBackprop)
            {
                Action backward = () =>
                {
                    res.ReleaseWeight();

                    using (Tensor tW2 = t2.TWeight.Transpose())
                    {
                        Ops.Addmm(t1.TGradient, 1.0f, t1.TGradient, 1.0f, res.TGradient, tW2);
                    }

                    using (Tensor tW1 = t1.TWeight.Transpose())
                    {
                        Ops.Addmm(t2.TGradient, 1.0f, t2.TGradient, 1.0f, tW1, res.TGradient);
                    }

                    res.Dispose();
                };
                m_backprop.Add(backward);

                t1.UnbindFromComputeGraph();
                t2.UnbindFromComputeGraph();
            }

            return res;
        }

        public IWeightTensor Affine(IWeightTensor m1, IWeightTensor m2, IWeightTensor mbias)
        {
            if (m1 == null)
            {
                throw new ArgumentNullException($"m1 tensor is null");
            }

            if (m2 == null)
            {
                throw new ArgumentNullException($"m2 tensor is null");
            }

            if (mbias == null)
            {
                throw new ArgumentNullException($"mbias tensor is null");
            }

            WeightTensor t1 = m1 as WeightTensor;
            WeightTensor t2 = m2 as WeightTensor;
            WeightTensor t3 = mbias as WeightTensor;

            int n = t1.Rows;
            int d = t2.Columns;
            WeightTensor res = m_weightTensorFactory.CreateWeightTensor(n, d, m_deviceId, name: $"{GetHashString(m1.Name, m2.Name, mbias.Name)}.Affine", graphToBind: this);
            VisualizeNodes(new IWeightTensor[] { m1, m2, mbias }, res);

            using (Tensor t3WExp = t3.TWeight.Expand(n, d))
            {
                Ops.Addmm(res.TWeight, 1.0f, t3WExp, 1.0f, t1.TWeight, t2.TWeight);
            }

            if (m_needsBackprop)
            {
                Action backward = () =>
                {
                    res.ReleaseWeight();

                    using (Tensor t3G = t3.TGradient.Expand(n, d))
                    {
                        Ops.Add(t3G, t3G, res.TGradient);
                    }

                    using (Tensor tW2 = t2.TWeight.Transpose())
                    {
                        Ops.Addmm(t1.TGradient, 1.0f, t1.TGradient, 1.0f, res.TGradient, tW2);
                    }

                    using (Tensor tW1 = t1.TWeight.Transpose())
                    {
                        Ops.Addmm(t2.TGradient, 1.0f, t2.TGradient, 1.0f, tW1, res.TGradient);
                    }

                    res.Dispose();
                };
                m_backprop.Add(backward);

                t1.UnbindFromComputeGraph();
                t2.UnbindFromComputeGraph();
            }

            return res;

        }

        public IWeightTensor Transpose(IWeightTensor w)
        {
            WeightTensor m = w as WeightTensor;
            WeightTensor res = m_weightTensorFactory.CreateWeightTensor(m.Columns, m.Rows, m_deviceId, name: $"{GetHashString(w.Name)}.Transpose", graphToBind: this);
            VisualizeNodes(w, res);

            res.TWeight = m.TWeight.Transpose();
            if (m_needsBackprop)
            {
                Action backward = () =>
                {
                    res.ReleaseWeight();
                    using (Tensor gT = res.TGradient.Transpose())
                    {
                        m.CopyOrAddGradient(gT);
                    }

                    res.Dispose();
                };
                m_backprop.Add(backward);
            }

            return res;
        }

        public int[] Argmax(IWeightTensor w, int dim)
        {
            int[] idx = null;
            WeightTensor m = w as WeightTensor;
            using (Tensor argMaxT = Ops.Argmax(null, m.TWeight, dim))
            {
                float[] res = new float[argMaxT.ElementCount()];
                argMaxT.CopyToArray(res);

                idx = new int[res.Length];
                for (int i = 0; i < res.Length; i++)
                {
                    idx[i] = (int)res[i];
                }
            }

            return idx;
        }

        public IWeightTensor Softmax(IWeightTensor w, bool runGradients = true, bool inPlace = false)
        {
            WeightTensor m = w as WeightTensor;
            WeightTensor res = null;

            if (inPlace)
            {
                res = m.CopyWeightsRef($"{GetHashString(w.Name)}.Softmax");               
            }
            else
            {
                res = m_weightTensorFactory.CreateWeightTensor(m.Sizes, m_deviceId, name: $"{GetHashString(w.Name)}.Softmax");
            }

            VisualizeNodes(w, res);

            Ops.Softmax(res.TWeight, m.TWeight);
            if (m_needsBackprop && runGradients)
            {
                Action backward = () =>
                {
                    if (inPlace)
                    {                        
                        m.TGradient = res.TGradient.CopyRef();
                    }

                    m.AddSoftmaxGradient(res, inPlace);
                    res.Dispose();
                };
                m_backprop.Add(backward);
            }

            return res;
        }

        public IWeightTensor PeekRow(IWeightTensor w, int ix, int num = 1, bool runGradients = true)
        {
            WeightTensor m = w as WeightTensor;
            WeightTensor res = m_weightTensorFactory.CreateWeightTensor(num, m.Columns, m_deviceId, name: $"{GetHashString(w.Name)}.PeekRow", graphToBind: this);
            res.TWeight = m.TWeight.Narrow(0, ix, num);
            res.TGradient = runGradients ? m.TGradient.Narrow(0, ix, num) : null;

            VisualizeNodes(w, res);

            if (m_needsBackprop && runGradients)
            {
                Action backward = () =>
                {
                    res.Dispose();
                };
                m_backprop.Add(backward);
            }

            return res;
        }

        private byte[] GetHash(string inputString)
        {
            HashAlgorithm algorithm = SHA256.Create();
            return algorithm.ComputeHash(Encoding.UTF8.GetBytes(inputString));
        }

        private string GetHashString(params string[] inputStrings)
        {
            if (m_visNeuralNetwork)
            {
                string inputString = string.Join("_", inputStrings);
                StringBuilder sb = new StringBuilder();
                foreach (byte b in GetHash(inputString))
                {
                    sb.Append(b.ToString("X2"));
                }

                return sb.ToString();
            }
            return string.Empty;
        }

        private void VisualizeNodes(IWeightTensor sourceNode, IWeightTensor targetNode)
        {
            // No-op in .NET Core.
        }

        private void VisualizeNodes(IEnumerable<IWeightTensor> sourceNodes, IWeightTensor targetNode)
        {
            // No-op in .NET Core.
        }

     
        public IWeightTensor RepeatRows(IWeightTensor w, int n, bool runGradient = true)
        {
            WeightTensor m = w as WeightTensor;
            WeightTensor res = m_weightTensorFactory.CreateWeightTensor(m.Rows * n, m.Columns, m_deviceId, name: $"{GetHashString(w.Name)}.RepeatRows", graphToBind: this);
            VisualizeNodes(w, res);

            res.TWeight = m.TWeight.RepeatTensor(n, 1);
            if (m_needsBackprop)
            {
                Action backward = () =>
                {
                    if (runGradient)
                    {
                        res.ReleaseWeight();
                        for (int i = 0; i < n; i++)
                        {
                            using (Tensor resG_i = res.TGradient.Narrow(0, m.Rows * i, m.Rows))
                            {
                                m.CopyOrAddGradient(resG_i);
                            }
                        }
                    }
                    res.Dispose();
                };
                m_backprop.Add(backward);
            }

            return res;
        }


        public IWeightTensor ConcatRows(List<IWeightTensor> wl)
        {
            if (wl.Count == 1)
            {
                return wl[0];
            }

            List<string> wlNameList = new List<string>();
            List<Tensor> twl = new List<Tensor>();
            int sx = 0;
            int sy = 0;
            foreach (IWeightTensor item in wl)
            {
                WeightTensor m = item as WeightTensor;
                sx += m.Rows;
                sy = m.Columns;

                twl.Add(m.TWeight);
                wlNameList.Add(item.Name);
            }

            string wlName = string.Join("_", wlNameList);
            WeightTensor res = m_weightTensorFactory.CreateWeightTensor(sx, sy, m_deviceId, name: $"{GetHashString(wlName)}.ConcatRows", graphToBind: this);
            VisualizeNodes(wl, res);

            Ops.Concat(res.TWeight, 0, twl.ToArray());

            if (m_needsBackprop)
            {
                Action backward = () =>
                {
                    res.ReleaseWeight();

                    sx = 0;
                    foreach (IWeightTensor item in wl)
                    {
                        WeightTensor m = item as WeightTensor;
                        using (Tensor tTmp = res.TGradient.Narrow(0, sx, m.Rows))
                        {
                            m.CopyOrAddGradient(tTmp);
                            sx += m.Rows;
                        }
                    }

                    res.Dispose();
                };
                m_backprop.Add(backward);
            }

            return res;
        }

        public IWeightTensor TransposeBatch(IWeightTensor m, int batchSize)
        {
            WeightTensor t = m as WeightTensor;
            WeightTensor res = m_weightTensorFactory.CreateWeightTensor(t.Sizes, m_deviceId, name: $"{GetHashString(m.Name)}.TransposeBatch", graphToBind: this);
            VisualizeNodes(m, res);

            int sizeEveryBatch = m.Rows / batchSize;
            using (Tensor tWView = t.TWeight.View(sizeEveryBatch, batchSize, m.Columns))
            {
                using (Tensor tWViewPermute = tWView.Permute(1, 0, 2))
                {
                    using (Tensor tW2 = Ops.AsContiguous(tWViewPermute))
                    {
                        res.TWeight = tW2.View(m.Rows, m.Columns);
                    }
                }
            }

            if (m_needsBackprop)
            {
                Action backward = () =>
                {
                    res.ReleaseWeight();

                    using (Tensor g = t.TGradient.View(sizeEveryBatch, batchSize, m.Columns))
                    {
                        using (Tensor t2 = res.TGradient.View(batchSize, sizeEveryBatch, m.Columns))
                        {
                            using (Tensor t2Permute = t2.Permute(1, 0, 2))
                            {
                                Ops.Add(g, g, t2Permute);
                            }
                        }
                    }

                    res.Dispose();
                };
                m_backprop.Add(backward);
            }

            return res;
        }

        public IWeightTensor ConcatColumns(params IWeightTensor[] wl)
        {
            if (wl.Length == 1)
            {
                return wl[0];
            }

            List<string> srcNameList = new List<string>();
            List<Tensor> twl = new List<Tensor>();
            int sx = 0;
            int sy = 0;

            foreach (IWeightTensor item in wl)
            {
                WeightTensor m = item as WeightTensor;
                sx = m.Rows;
                sy += m.Columns;

                twl.Add(m.TWeight);
                srcNameList.Add(item.Name);
            }

            string srcNames = string.Join("_", srcNameList);
            WeightTensor res = m_weightTensorFactory.CreateWeightTensor(sx, sy, m_deviceId, name: $"{GetHashString(srcNames)}.ConcatColumns", graphToBind: this);
            VisualizeNodes(wl, res);

            Ops.Concat(res.TWeight, 1, twl.ToArray());
            if (m_needsBackprop)
            {
                Action backward = () =>
                {
                    res.ReleaseWeight();

                    sy = 0;
                    foreach (IWeightTensor item in wl)
                    {
                        WeightTensor m = item as WeightTensor;
                        using (Tensor tTmp = res.TGradient.Narrow(1, sy, m.Columns))
                        {
                            m.CopyOrAddGradient(tTmp);
                            sy += m.Columns;
                        }
                    }

                    res.Dispose();
                };
                m_backprop.Add(backward);
            }

            return res;
        }

        public List<IWeightTensor> SplitColumns2(IWeightTensor w, params int[] sizes)
        {
            WeightTensor m = w as WeightTensor;
            List<IWeightTensor> resList = new List<IWeightTensor>();

            int x = 0;
            foreach (int size in sizes)
            {
                WeightTensor res = m_weightTensorFactory.CreateWeightTensor(m.Rows, size, m_deviceId, name: $"{GetHashString(w.Name)}.SplitColumn", graphToBind: this);
                VisualizeNodes(w, res);

                res.TWeight = m.TWeight.Narrow(1, x, size);
                resList.Add(res);

                x += size;
            }


            if (m_needsBackprop)
            {
                Action backward = () =>
                {
                    x = 0;
                    int i = 0;
                    foreach (IWeightTensor item in resList)
                    {
                        WeightTensor item_i = item as WeightTensor;
                        using (Tensor mG = m.TGradient.Narrow(1, x, sizes[i]))
                        {
                            Ops.Add(mG, mG, item_i.TGradient);
                        }

                        item.Dispose();

                        x += sizes[i];
                        i++;
                    }
                };
                m_backprop.Add(backward);
            }


            return resList;
        }

        public IWeightTensor Permute(IWeightTensor w, params int[] dims)
        {
            WeightTensor m = w as WeightTensor;
            WeightTensor res = m_weightTensorFactory.CreateWeightTensor(m.Sizes, m_deviceId, name: $"{GetHashString(w.Name)}.Permute", graphToBind: this);
            VisualizeNodes(w, res);

            using (Tensor tWPremute = m.TWeight.Permute(dims))
            {
                res.TWeight = Ops.AsContiguous(tWPremute);
            }

            if (m_needsBackprop)
            {
                Action backward = () =>
                {
                    res.ReleaseWeight();

                    using (Tensor gT = m.TGradient.Permute(dims))
                    {
                        Ops.Add(gT, gT, res.TGradient);
                    }
                    res.Dispose();
                };
                m_backprop.Add(backward);
            }

            return res;
        }


        public IWeightTensor View(IWeightTensor w, params long[] dims)
        {
            bool hasNegOne = false;
            int negOneIdx = 0;
            long totalGivenSize = 1;
            for (int i = 0;i < dims.Length;i++)
            {
                long dim = dims[i];
                if (dim == -1)
                {
                    if (hasNegOne)
                    {
                        throw new ArgumentException($"View operation only allows single -1 in dims.");
                    }

                    hasNegOne = true;
                    negOneIdx = i;
                }
                else
                {
                    totalGivenSize *= dim;
                }
            }

            if (hasNegOne)
            {
                long totalSrcSize = 1;
                foreach (int size in w.Sizes)
                {
                    totalSrcSize *= size;
                }

                dims[negOneIdx] = totalSrcSize / totalGivenSize;
            }


            WeightTensor m = w as WeightTensor;
            WeightTensor res = m_weightTensorFactory.CreateWeightTensor(dims, m_deviceId, name: w.Name, graphToBind: this);
            //  VisualizeNodes(w, res);


            using (Tensor congtiW = Ops.AsContiguous(m.TWeight))
            {
                res.TWeight = congtiW.View(dims);
            }

            if (m_needsBackprop)
            {
                Action backward = () =>
                {
                    res.ReleaseWeight();

                    using (Tensor resG = res.TGradient.View(m.Sizes))
                    {
                        m.CopyOrAddGradient(resG);
                    }
                    res.Dispose();
                };
                m_backprop.Add(backward);
            }

            return res;
        }


        public IWeightTensor Expand(IWeightTensor w, params long[] dims)
        {
            
            WeightTensor m = w as WeightTensor;
            WeightTensor res = m_weightTensorFactory.CreateWeightTensor(dims, m_deviceId, name: $"{GetHashString(w.Name)}.Expand", graphToBind: this);
            VisualizeNodes(w, res);

            res.TWeight = m.TWeight.Expand(dims);

            if (m_needsBackprop)
            {
                Action backward = () =>
                {
                    res.ReleaseWeight();

                    using (var mGExp = m.TGradient.Expand(dims))
                    {
                        Ops.Add(mGExp, mGExp, res.TGradient);
                    }
                    res.Dispose();
                };
                m_backprop.Add(backward);
            }

            return res;
        }

        public (IWeightTensor r1, IWeightTensor r2) SplitColumns(IWeightTensor w, int size1, int size2)
        {
            List<IWeightTensor> res = SplitColumns2(w, size1, size2);

            return (res[0], res[1]);
        }

        public (IWeightTensor r1, IWeightTensor r2, IWeightTensor r3) SplitColumns(IWeightTensor w, int size1, int size2, int size3)
        {
            List<IWeightTensor> res = SplitColumns2(w, size1, size2, size3);

            return (res[0], res[1], res[2]);
        }

        private readonly SeedSource seedSource = new SeedSource(DateTime.Now.Millisecond);
        private Tensor BuildRandomTensor(int rows, int columns, int batchSize, float prob)
        {
            using (Tensor noise = new Tensor(TensorAllocator.Allocator(m_deviceId), DType.Float32, rows / batchSize, columns))
            {
                Ops.RandomBernoulli(noise, seedSource, prob);

                if (rows / batchSize == 1)
                {
                    return noise.Expand(rows, columns);
                }
                else
                {
                    return noise.RepeatTensor(batchSize, 1);
                }
            }
        }

        public IWeightTensor LayerNorm(IWeightTensor src, IWeightTensor alpha, IWeightTensor beta, float eps = 1e-09f)
        {
            WeightTensor srcT = src as WeightTensor;
            WeightTensor alphaT = alpha as WeightTensor;
            WeightTensor betaT = beta as WeightTensor;

            Tensor alphaTWExp = alphaT.TWeight.Expand(src.Rows, src.Columns);
            Tensor betaTWExp = betaT.TWeight.Expand(src.Rows, src.Columns);

            WeightTensor res = m_weightTensorFactory.CreateWeightTensor(srcT.Sizes, m_deviceId, name: $"{GetHashString(src.Name, alpha.Name, beta.Name)}.LayerNorm");
            VisualizeNodes(new IWeightTensor[] { src, alpha, beta }, res);

            Ops.LayerNorm(res.TWeight, srcT.TWeight, alphaTWExp, betaTWExp, eps);
            if (m_needsBackprop)
            {
                Action backward = () =>
                {
                    using (Tensor alphaTGExp = alphaT.TGradient.Expand(src.Rows, src.Columns))
                    {
                        using (Tensor betaTGExp = betaT.TGradient.Expand(src.Rows, src.Columns))
                        {
                            Ops.LayerNormGrad(srcT.TGradient, alphaTGExp, betaTGExp, res.TGradient, res.TWeight, srcT.TWeight, alphaTWExp, betaTWExp, eps);
                        }
                    }

                    alphaTWExp.Dispose();
                    betaTWExp.Dispose();

                    res.Dispose();
                };
                m_backprop.Add(backward);

                srcT.UnbindFromComputeGraph();
            }
            else
            {
                alphaTWExp.Dispose();
                betaTWExp.Dispose();
            }

            return res;
        }



        /// <summary>
        /// LayerNorm (src1 + src2)
        /// </summary>
        /// <param name="src1"></param>
        /// <param name="src2"></param>
        /// <param name="alpha"></param>
        /// <param name="beta"></param>
        /// <param name="eps"></param>
        /// <returns></returns>
        public IWeightTensor AddLayerNorm(IWeightTensor src1, IWeightTensor src2, IWeightTensor alpha, IWeightTensor beta, float eps = 1e-09f)
        {
            WeightTensor src1T = src1 as WeightTensor;
            WeightTensor src2T = src2 as WeightTensor;
            WeightTensor alphaT = alpha as WeightTensor;
            WeightTensor betaT = beta as WeightTensor;

            Tensor alphaTWExp = alphaT.TWeight.Expand(src1.Rows, src1.Columns);
            Tensor betaTWExp = betaT.TWeight.Expand(src1.Rows, src1.Columns);

            WeightTensor res = m_weightTensorFactory.CreateWeightTensor(src1T.Sizes, m_deviceId, name: $"{GetHashString(src1.Name, src2.Name, alpha.Name, beta.Name)}.AddLayerNorm");
            VisualizeNodes(new IWeightTensor[] { src1, src2, alpha, beta }, res);

            Ops.AddLayerNorm(res.TWeight, src1T.TWeight, src2T.TWeight, alphaTWExp, betaTWExp, eps);
            if (m_needsBackprop)
            {
                Action backward = () =>
                {
                    using (Tensor alphaTGExp = alphaT.TGradient.Expand(src1.Rows, src1.Columns))
                    {
                        using (Tensor betaTGExp = betaT.TGradient.Expand(src1.Rows, src1.Columns))
                        {
                            Ops.AddLayerNormGrad(src1T.TGradient, src2T.TGradient, alphaTGExp, betaTGExp, res.TGradient, res.TWeight, src1T.TWeight, src2T.TWeight, alphaTWExp, betaTWExp, eps);
                        }
                    }

                    alphaTWExp.Dispose();
                    betaTWExp.Dispose();

                    res.Dispose();
                };
                m_backprop.Add(backward);

                src1T.UnbindFromComputeGraph();
                src2T.UnbindFromComputeGraph();
            }
            else
            {
                alphaTWExp.Dispose();
                betaTWExp.Dispose();
            }

            return res;
        }


        public IWeightTensor Dropout(IWeightTensor V, int batchSize, float drop_prob, bool inPlace = false)
        {
            if (drop_prob == 0 || !m_needsBackprop)
            {
                return V;
            }

            // Generate noise tensor
            float p = 1.0f - drop_prob;
            Tensor noise = BuildRandomTensor(V.Rows, V.Columns, batchSize, p);

            WeightTensor w = V as WeightTensor;
            WeightTensor res = null;
            if (inPlace)
            {
                res = w.CopyWeightsRef($"{GetHashString(V.Name)}.Dropout");
            }
            else
            {
                res = m_weightTensorFactory.CreateWeightTensor(w.Sizes, m_deviceId, name: $"{GetHashString(V.Name)}.Dropout", graphToBind: this);
            }
            VisualizeNodes(V, res);

            Ops.Mul(res.TWeight, w.TWeight, noise);
            
            Action backward = () =>
             {
                 res.ReleaseWeight();

                 if (inPlace)
                 {
                     w.TGradient = res.TGradient.CopyRef();
                 }

                 w.AddMulGradient(noise, res.TGradient, inPlace);

                 res.Dispose();
                 noise.Dispose();
             };
            m_backprop.Add(backward);


            return res;
        }

        public void Dispose()
        {
            // We only dispose root computing graph, For sub graph, we don't do it.
            if (m_isSubGraph == false)
            {
                if (m_backprop != null)
                {
                    m_backprop.Clear();
                }

                if (m_weightTensorFactory != null)
                {
                    m_weightTensorFactory.Dispose();
                }
            }
            else
            {
                foreach (WeightTensor item in m_tensorsBindToCurrentGraph)
                {
                    item.ReleaseWeight();
                }
            }

            m_tensorsBindToCurrentGraph.Clear();
        }
    }
}
