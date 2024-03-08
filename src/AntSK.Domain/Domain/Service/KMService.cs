﻿using AntSK.Domain.Common.DependencyInjection;
using AntSK.Domain.Domain.Interface;
using Microsoft.KernelMemory;
using AntSK.Domain.Utils;
using AntSK.Domain.Domain.Dto;
using AntSK.Domain.Options;
using Microsoft.KernelMemory.ContentStorage.DevTools;
using Microsoft.KernelMemory.FileSystem.DevTools;
using Microsoft.KernelMemory.Postgres;
using System.Net.Http;
using Microsoft.Extensions.Options;
using Microsoft.KernelMemory.Configuration;
using Microsoft.Extensions.Configuration;
using AntSK.Domain.Repositories;
using LLamaSharp.KernelMemory;
using LLama.Common;
using DocumentFormat.OpenXml.Spreadsheet;
using LLama;

namespace AntSK.Domain.Domain.Service
{
    [ServiceDescription(typeof(IKMService), ServiceLifetime.Scoped)]
    public class KMService(
           IConfiguration _config,
           IKmss_Repositories _kmss_Repositories,
           IAIModels_Repositories _aIModels_Repositories
        ) : IKMService
    {
        public MemoryServerless GetMemory()
        {
            var memory = new KernelMemoryBuilder();
            //加载向量库
            WithMemoryDbByVectorDB(memory, _config);
            var result = memory.Build<MemoryServerless>();
            return result;
        }


        public MemoryServerless GetMemoryByKMS(string kmsID, SearchClientConfig searchClientConfig = null)
        {
            //获取KMS配置
            var kms = _kmss_Repositories.GetFirst(p => p.Id == kmsID);
            var chatModel = _aIModels_Repositories.GetFirst(p => p.Id == kms.ChatModelID);
            var embedModel = _aIModels_Repositories.GetFirst(p => p.Id == kms.EmbeddingModelID);

            //http代理
            var chatHttpClient = OpenAIHttpClientHandlerUtil.GetHttpClient(chatModel.EndPoint);
            var embeddingHttpClient = OpenAIHttpClientHandlerUtil.GetHttpClient(embedModel.EndPoint);

            //搜索配置
            if (searchClientConfig.IsNull())
            {
                searchClientConfig = new SearchClientConfig
                {
                    MaxAskPromptSize = 2048,
                    MaxMatchesCount = 3,
                    AnswerTokens = 1000,
                    EmptyAnswer = "知识库未搜索到相关内容"
                };
            }

            var memory = new KernelMemoryBuilder()
            .WithSearchClientConfig(searchClientConfig)
            .WithCustomTextPartitioningOptions(new TextPartitioningOptions
            {
                MaxTokensPerLine = kms.MaxTokensPerLine,
                MaxTokensPerParagraph = kms.MaxTokensPerParagraph,
                OverlappingTokens = kms.OverlappingTokens
            });
            //加载huihu 模型
            WithTextGenerationByAIType(memory, chatModel, chatHttpClient);
            //加载向量模型
            WithTextEmbeddingGenerationByAIType(memory,embedModel, embeddingHttpClient);
            //加载向量库
            WithMemoryDbByVectorDB(memory, _config);

            var result = memory.Build<MemoryServerless>();
            return result;

        }

        private void WithTextEmbeddingGenerationByAIType(IKernelMemoryBuilder memory,AIModels embedModel, HttpClient embeddingHttpClient )
        {
            switch (embedModel.AIType)
            {
                case Model.Enum.AIType.OpenAI:
                    memory.WithOpenAITextEmbeddingGeneration(new OpenAIConfig()
                    {
                        APIKey = embedModel.ModelKey,
                        EmbeddingModel = embedModel.ModelName
                    }, null, false, embeddingHttpClient);
                    break;
                case Model.Enum.AIType.AzureOpenAI:
                    memory.WithAzureOpenAITextEmbeddingGeneration(new AzureOpenAIConfig()
                    {
                        APIKey = embedModel.ModelKey,
                        Deployment = embedModel.ModelName.ConvertToString(),
                        Endpoint = embedModel.EndPoint                         
                    });
                    break;
                case Model.Enum.AIType.LLamaSharp:
                    InferenceParams infParams = new() { AntiPrompts = ["\n\n"] };
                    LLamaSharpConfig lsConfig = new(embedModel.ModelName) { DefaultInferenceParams = infParams };
                    var parameters = new ModelParams(lsConfig.ModelPath)
                    {
                        ContextSize = lsConfig?.ContextSize ?? 2048,
                        Seed = lsConfig?.Seed ?? 0,
                        GpuLayerCount = lsConfig?.GpuLayerCount ?? 20,
                        EmbeddingMode = true
                    };
                    var weights = LLamaWeights.LoadFromFile(parameters);
                    var embedder = new LLamaEmbedder(weights, parameters);
                    memory.WithLLamaSharpTextEmbeddingGeneration(new LLamaSharpTextEmbeddingGenerator(embedder));
                    break;
            }
        }

        private void WithTextGenerationByAIType(IKernelMemoryBuilder memory,AIModels chatModel, HttpClient chatHttpClient )
        {
            switch (chatModel.AIType)
            {
                case Model.Enum.AIType.OpenAI:
                    memory.WithOpenAITextGeneration(new OpenAIConfig()
                    {
                        APIKey = chatModel.ModelKey,
                        TextModel = chatModel.ModelName
                    }, null, chatHttpClient);
                    break;
                case Model.Enum.AIType.AzureOpenAI:
                    memory.WithAzureOpenAITextGeneration(new AzureOpenAIConfig()
                    {
                        APIKey = chatModel.ModelKey,
                        Deployment = chatModel.ModelName.ConvertToString(),
                        Endpoint = chatModel.EndPoint
                    });
                    break;
                case Model.Enum.AIType.LLamaSharp:
                    InferenceParams infParams = new() { AntiPrompts = ["\n\n"] };
                    LLamaSharpConfig lsConfig = new(chatModel.ModelName) { DefaultInferenceParams = infParams };
                    var parameters = new ModelParams(lsConfig.ModelPath)
                    {
                        ContextSize = lsConfig?.ContextSize ?? 2048,
                        Seed = lsConfig?.Seed ?? 0,
                        GpuLayerCount = lsConfig?.GpuLayerCount ?? 20,
                        EmbeddingMode = true
                    };
                    var weights = LLamaWeights.LoadFromFile(parameters);
                    var context = weights.CreateContext(parameters);
                    var executor = new StatelessExecutor(weights, parameters);
                    memory.WithLLamaSharpTextGeneration(new LlamaSharpTextGenerator(weights, context, executor, lsConfig?.DefaultInferenceParams));
                    break;
            }
        }

        private void WithMemoryDbByVectorDB(IKernelMemoryBuilder memory,IConfiguration _config)
        {
            string VectorDb = _config["KernelMemory:VectorDb"].ConvertToString();
            string ConnectionString = _config["KernelMemory:ConnectionString"].ConvertToString();
            string TableNamePrefix = _config["KernelMemory:TableNamePrefix"].ConvertToString();
            switch (VectorDb)
            {
                case "Postgres":
                    memory.WithPostgresMemoryDb(new PostgresConfig()
                    {
                        ConnectionString = ConnectionString,
                        TableNamePrefix = TableNamePrefix
                    });
                    break;
                case "Disk":
                    memory.WithSimpleFileStorage(new SimpleFileStorageConfig()
                    {
                        StorageType = FileSystemTypes.Disk
                    });
                    break;
                case "Memory":
                    memory.WithSimpleFileStorage(new SimpleFileStorageConfig()
                    {
                        StorageType = FileSystemTypes.Volatile
                    });
                    break;
            }
        }

        public async Task<List<KMFile>> GetDocumentByFileID(string kmsid,string fileid)
        {
            var _memory = GetMemoryByKMS(kmsid);
            var memories = await _memory.ListIndexesAsync();
            var memoryDbs = _memory.Orchestrator.GetMemoryDbs();
            List<KMFile> docTextList = new List<KMFile>();

            foreach (var memoryIndex in memories)
            {
                foreach (var memoryDb in memoryDbs)
                {

                    var items = await memoryDb.GetListAsync(memoryIndex.Name, new List<MemoryFilter>() { new MemoryFilter().ByDocument(fileid) }, 100, true).ToListAsync();
                    foreach (var item in items)
                    {
                        KMFile file = new KMFile()
                        {
                            Text = item.Payload.FirstOrDefault(p => p.Key == "text").Value.ConvertToString(),
                            Url = item.Payload.FirstOrDefault(p => p.Key == "url").Value.ConvertToString(),
                            LastUpdate = item.Payload.FirstOrDefault(p => p.Key == "last_update").Value.ConvertToString(),
                            Schema = item.Payload.FirstOrDefault(p => p.Key == "schema").Value.ConvertToString(),
                            File = item.Payload.FirstOrDefault(p => p.Key == "file").Value.ConvertToString(),
                        };
                        docTextList.Add(file);
                    }
                }
            }
            return docTextList;
        }
    }
}
