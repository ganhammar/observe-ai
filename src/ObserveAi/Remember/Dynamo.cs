using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace ObserveAi;

/// <summary>The one DynamoDB operation the pipeline uses, shared by SeenStore and Caps.</summary>
public static class Dynamo
{
    public delegate Task<UpdateItemResponse> UpdateItem(UpdateItemRequest request, CancellationToken cancellationToken);

    public static UpdateItem Against(IAmazonDynamoDB dynamo) => dynamo.UpdateItemAsync;
}
